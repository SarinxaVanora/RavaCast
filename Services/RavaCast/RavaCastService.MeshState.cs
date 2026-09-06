using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.SubKinds;
using GameControl = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using GameKernelDevice = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device;
using GameRenderTargetManager = FFXIVClientStructs.FFXIV.Client.Graphics.Render.RenderTargetManager;
using Dalamud.Plugin.Services;
using MessagePack;
using Microsoft.Extensions.Logging;
using RavaCast.Configuration;
using RavaCast.Services.Mediator;
using RavaCast.Services.Mesh;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace RavaCast.Services.RavaCast;

public sealed partial class RavaCastService
{

    private void OnGameMesh(MeshPayloadMessage msg)
    {
        if (!TryReadEnvelope(msg.Payload, out var env) || env is null) return;

        try
        {
            // Area discovery is intentionally multicast to everyone in the same live game instance.
            // Only surface an area advertisement/state when the named host is actually present in our
            // local object table, preserving the user-facing "nearby" behaviour without needing to
            // derive the host's private route up front. Targeted join/state traffic is never filtered.
            var currentAreaSessionId = _dalamudUtil.GetAreaSessionId();
            var isAreaDelivery = !string.IsNullOrWhiteSpace(currentAreaSessionId)
                && string.Equals(msg.TargetSessionId, currentAreaSessionId, StringComparison.Ordinal);
            if (isAreaDelivery && env.Payload is not null && (env.Op == RavaCastOp.Advertise || env.Op == RavaCastOp.StateSnapshot))
            {
                var discoveryState = MessagePackSerializer.Deserialize<RavaCastStatePayload>(env.Payload);
                if (!_dalamudUtil.IsPlayerNameVisible(discoveryState.HostName))
                    return;
            }

            switch (env.Op)
            {
                case RavaCastOp.Advertise:
                    HandleAdvertise(env);
                    break;
                case RavaCastOp.Join:
                    HandleJoin(msg.FromSessionId, env);
                    break;
                case RavaCastOp.Leave:
                    HandleLeave(env);
                    break;
                case RavaCastOp.StateSnapshot:
                    HandleStateSnapshot(env);
                    break;
                case RavaCastOp.RequestState:
                    HandleRequestState(msg.FromSessionId, env);
                    break;
                case RavaCastOp.ScreenClosed:
                    HandleScreenClosed(env);
                    break;
                case RavaCastOp.ConsentCookies:
                    HandleConsentCookies(env);
                    break;
                case RavaCastOp.DirectStreamStart:
                    HandleDirectStreamStart(msg.FromSessionId, env);
                    break;
                case RavaCastOp.DirectStreamStop:
                    HandleDirectStreamStop(env);
                    break;
                case RavaCastOp.DirectStreamViewerReady:
                    HandleDirectStreamViewerReady(msg.FromSessionId, env);
                    break;
                case RavaCastOp.DirectStreamViewerLeft:
                    HandleDirectStreamViewerLeft(msg.FromSessionId, env);
                    break;
                case RavaCastOp.DirectStreamOffer:
                case RavaCastOp.DirectStreamAnswer:
                case RavaCastOp.DirectStreamIce:
                    HandleDirectStreamSignal(msg.FromSessionId, env);
                    break;
                case RavaCastOp.DirectStreamSignalChunk:
                    HandleDirectStreamSignalChunk(msg.FromSessionId, env);
                    break;
                case RavaCastOp.DirectStreamStats:
                    HandleDirectStreamStats(env);
                    break;
                case RavaCastOp.DirectStreamError:
                    HandleDirectStreamError(msg.FromSessionId, env);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to handle RavaCast mesh payload");
        }
    }


    private void StoreActiveSummary(RavaCastSummary summary)
    {
        _activeCasts.AddOrUpdate(summary.CastId, summary, (_, existing) => summary.NavigationRevision < existing.NavigationRevision ? existing : summary);
    }

    private void HandleAdvertise(RavaCastEnvelope env)
    {
        if (env.Payload is null) return;
        var state = MessagePackSerializer.Deserialize<RavaCastStatePayload>(env.Payload);
        var summary = ToSummary(env.CastId, state, Environment.TickCount64);
        StoreActiveSummary(summary);

        if (_joined is not null && _joined.CastId == env.CastId)
            ApplyState(summary, _joined.IsMuted);
    }

    private void HandleStateSnapshot(RavaCastEnvelope env)
    {
        if (env.Payload is null) return;
        var state = MessagePackSerializer.Deserialize<RavaCastStatePayload>(env.Payload);
        var summary = ToSummary(env.CastId, state, Environment.TickCount64);
        StoreActiveSummary(summary);

        if (_pendingJoinMuted.TryRemove(env.CastId, out var muted))
        {
            _pendingPasswordProtectedJoinRetries.TryRemove(env.CastId, out _);
            ApplyState(summary, muted);
        }
        else if (_joined is not null && _joined.CastId == env.CastId)
            ApplyState(summary, _joined.IsMuted);
    }

    private void HandleJoin(string fromSessionId, RavaCastEnvelope env)
    {
        if (_hosted is null || _hosted.CastId != env.CastId || env.Payload is null) return;
        var payload = MessagePackSerializer.Deserialize<RavaCastJoinPayload>(env.Payload);
        var viewerSession = !string.IsNullOrWhiteSpace(payload.ViewerSessionId) ? payload.ViewerSessionId : fromSessionId;
        if (string.IsNullOrWhiteSpace(viewerSession)) return;
        if (_hosted.PasswordProtected && !string.Equals(payload.PasswordHash ?? string.Empty, _hosted.PasswordHash, StringComparison.Ordinal))
        {
            _logger.LogWarning("Rejected RavaCast join from {viewerSession}: wrong password for cast {castId}.", viewerSession, env.CastId);
            return;
        }
        _joinedViewers[viewerSession] = true;
        SendHostedState(viewerSession, RavaCastOp.StateSnapshot);
        if (_hosted.PasswordProtected)
            _ = RepeatAcceptedPasswordProtectedJoinStateAsync(_hosted.CastId, viewerSession);
        if (_hosted.Mode == RavaCastMode.DirectStream)
        {
            StartHostedDirectStreamPublisher(notifyExistingViewers: false);
            SendDirectStreamStart(viewerSession);
            if (_hosted.PasswordProtected)
                _ = RepeatDirectStreamStartForAcceptedPasswordProtectedJoinAsync(_hosted.CastId, viewerSession);
        }
        BroadcastHostedStateNearby(RavaCastOp.Advertise);
    }

    private async Task RepeatAcceptedPasswordProtectedJoinStateAsync(Guid castId, string viewerSession)
    {
        try
        {
            foreach (var delayMs in new[] { 250, 800, 1600 })
            {
                await Task.Delay(delayMs).ConfigureAwait(false);
                if (_hosted is null || _hosted.CastId != castId || !_joinedViewers.ContainsKey(viewerSession)) return;
                SendHostedState(viewerSession, RavaCastOp.StateSnapshot);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to repeat accepted RavaCast password-protected join state");
        }
    }

    private async Task RepeatDirectStreamStartForAcceptedPasswordProtectedJoinAsync(Guid castId, string viewerSession)
    {
        try
        {
            foreach (var delayMs in new[] { 500, 1400, 2600 })
            {
                await Task.Delay(delayMs).ConfigureAwait(false);
                if (_hosted is null || _hosted.CastId != castId || _hosted.Mode != RavaCastMode.DirectStream || !_joinedViewers.ContainsKey(viewerSession)) return;
                SendDirectStreamStart(viewerSession);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to repeat RavaCast Direct Stream start after accepted password-protected join");
        }
    }

    private void HandleLeave(RavaCastEnvelope env)
    {
        if (_hosted is null || _hosted.CastId != env.CastId || env.Payload is null) return;
        var payload = MessagePackSerializer.Deserialize<RavaCastLeavePayload>(env.Payload);
        if (!string.IsNullOrWhiteSpace(payload.ViewerSessionId))
        {
            _joinedViewers.TryRemove(payload.ViewerSessionId, out _);
            _directStreamReadyViewers.TryRemove(payload.ViewerSessionId, out _);
            _surface.RemoveDirectStreamPeer(payload.ViewerSessionId);
            StopHostedDirectStreamIfNoViewers();
        }
        BroadcastHostedStateNearby(RavaCastOp.Advertise);
    }

    private void HandleRequestState(string fromSessionId, RavaCastEnvelope env)
    {
        if (_hosted is null || _hosted.CastId != env.CastId) return;
        var target = !string.IsNullOrWhiteSpace(fromSessionId) ? fromSessionId : string.Empty;
        if (!string.IsNullOrWhiteSpace(target))
            SendHostedState(target, RavaCastOp.StateSnapshot);
    }

    private void HandleScreenClosed(RavaCastEnvelope env)
    {
        _activeCasts.TryRemove(env.CastId, out _);
        _pendingJoinMuted.TryRemove(env.CastId, out _);
        _pendingPasswordProtectedJoinRetries.TryRemove(env.CastId, out _);
        RemoveDirectStreamSignalAssembliesForCast(env.CastId);
        if (_joined is not null && _joined.CastId == env.CastId)
        {
            _surface.StopDirectStreamReceiver();
            _joined = null;
            _surface.Close();
        }
    }

    private void ApplyState(RavaCastSummary summary, bool muted, string? viewerSessionIdOverride = null)
    {
        var previous = _joined;
        var sameCast = previous is not null && previous.CastId == summary.CastId;
        if (sameCast && summary.NavigationRevision < previous!.NavigationRevision)
            return;

        var sameUrl = previous is not null && string.Equals(previous.Url, summary.Url, StringComparison.OrdinalIgnoreCase);
        var effectiveMuted = sameCast ? previous!.IsMuted : muted;
        var effectiveVolume = sameCast ? previous!.Volume : _localVolume;
        var effectiveViewerSessionId = !string.IsNullOrWhiteSpace(viewerSessionIdOverride)
            ? viewerSessionIdOverride
            : sameCast && !string.IsNullOrWhiteSpace(previous!.ViewerSessionId)
                ? previous.ViewerSessionId
                : GetMySessionId();
        var sameDirectStreamRequest = sameCast && previous!.Mode == summary.Mode && previous.DirectStreamQuality == summary.DirectStreamQuality;
        var currentDirectStreamStatus = _surface.DirectStreamStatus;
        var directStreamReceiverRequested = sameDirectStreamRequest && previous.DirectStreamReceiverRequested && currentDirectStreamStatus.ReceiverActive;
        var effectiveDirectStreamStatus = sameCast && !string.IsNullOrWhiteSpace(previous!.DirectStreamStatus) ? previous.DirectStreamStatus : summary.DirectStreamStatus;
        var effectiveDirectStreamDetail = sameCast && !string.IsNullOrWhiteSpace(previous!.DirectStreamDetail) ? previous.DirectStreamDetail : summary.DirectStreamDetail;
        // URL Share viewers should only re-open when the host promotes a new shared URL revision.
        // The renderer's CurrentUrl can still differ briefly while media sites redirect or mutate history,
        // so do not compare against local CurrentUrl here.
        var shouldOpenSurface = !sameCast || !sameUrl || !_surface.IsOpen;
        // URL Share should not run a network-style clock against the page. The local browser owns
        // its own audio/video clock; RavaCast only uses host media state as the initial join/new-URL
        // position, then leaves the browser to stay internally synced.
        var effectivePlaybackPosition = Math.Max(0, summary.PositionSeconds);

        _joined = new JoinedCast
        {
            CastId = summary.CastId,
            HostSessionId = summary.HostSessionId,
            ViewerSessionId = effectiveViewerSessionId,
            HostName = summary.HostName,
            CastName = summary.CastName,
            Url = summary.Url,
            SourceDomain = summary.SourceDomain,
            MediaTitle = summary.MediaTitle,
            IsPlaying = summary.IsPlaying,
            PositionSeconds = effectivePlaybackPosition,
            DurationSeconds = summary.DurationSeconds,
            StateUtc = summary.StateUtc,
            JoinedCount = summary.JoinedCount,
            Plane = summary.Plane,
            Queue = summary.Queue,
            ConsentCookies = summary.ConsentCookies,
            PasswordProtected = summary.PasswordProtected,
            PasswordSalt = summary.PasswordSalt,
            IsMuted = effectiveMuted,
            Volume = effectiveVolume,
            Mode = summary.Mode,
            DirectStreamQuality = summary.DirectStreamQuality,
            DirectStreamStatus = effectiveDirectStreamStatus,
            DirectStreamDetail = effectiveDirectStreamDetail,
            DirectStreamReceiverRequested = directStreamReceiverRequested,
            NavigationRevision = summary.NavigationRevision
        };

        if (summary.Mode == RavaCastMode.DirectStream)
        {
            // Direct Stream viewers must not fall back to opening/controlling the shared URL locally. The
            // receiver surface is supplied by the Direct Stream bridge; if that bridge fails, show the error
            // instead of silently behaving like URL Share.
            if (previous?.DirectStreamReceiverRequested != true && _surface.IsOpen)
                _surface.Close();
            StartJoinedDirectStreamReceiver(summary);
            return;
        }

        // Switching back to URL Share must forcibly leave any previous Direct Stream receiver visual state.
        // A stale receiver texture can otherwise keep the RavaCast surface black while the local browser/audio are fine.
        if (previous?.DirectStreamReceiverRequested == true)
            _surface.StopDirectStreamReceiver();

        _surface.ApplySharedConsentCookies(summary.Url, summary.ConsentCookies);
        if (shouldOpenSurface)
            _surface.Open(summary.Url, effectiveMuted, effectiveVolume);
        _surface.ApplyMediaState(effectivePlaybackPosition, summary.IsPlaying, force: shouldOpenSurface || !sameCast || !sameUrl);

        // Only navigate the local URL Share browser when the host URL revision changed
        // or the surface had to be opened. The host promotes real browser URL changes
        // through NavigationRevision, so normal repeated state refreshes stay harmless.
    }

    private static RavaCastSummary BuildSummaryFromJoined(JoinedCast joined)
        => new(joined.CastId, joined.HostSessionId, joined.HostName, joined.CastName, joined.Url, joined.SourceDomain, joined.MediaTitle, joined.IsPlaying,
            joined.PositionSeconds, joined.DurationSeconds, joined.StateUtc, joined.JoinedCount, joined.Plane, joined.Queue, joined.ConsentCookies, Environment.TickCount64)
        {
            Mode = joined.Mode,
            PasswordProtected = joined.PasswordProtected,
            PasswordSalt = joined.PasswordSalt,
            DirectStreamQuality = joined.DirectStreamQuality,
            DirectStreamStatus = joined.DirectStreamStatus,
            DirectStreamDetail = joined.DirectStreamDetail,
            NavigationRevision = joined.NavigationRevision
        };

    private RavaCastSummary ToSummary(Guid castId, RavaCastStatePayload state, long lastSeenTick)
    {
        var stateUtc = DateTimeOffset.FromUnixTimeMilliseconds(state.StateUnixMs <= 0 ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : state.StateUnixMs).UtcDateTime;
        return new RavaCastSummary(castId, state.HostSessionId ?? string.Empty, state.HostName ?? "Player", state.CastName ?? "RavaCast",
            state.Url ?? string.Empty, state.SourceDomain ?? string.Empty, state.MediaTitle ?? string.Empty, state.IsPlaying,
            Math.Max(0, state.PositionSeconds), state.DurationSeconds, stateUtc, Math.Max(0, state.JoinedCount), state.Plane.ToPlane(), state.Queue ?? [], state.ConsentCookies ?? [], lastSeenTick)
        {
            Mode = state.Mode,
            PasswordProtected = state.PasswordProtected,
            PasswordSalt = state.PasswordSalt ?? string.Empty,
            DirectStreamQuality = state.DirectStreamQuality,
            DirectStreamStatus = state.DirectStreamStatus ?? string.Empty,
            DirectStreamDetail = state.DirectStreamDetail ?? string.Empty,
            DirectStreamNativeMediaAvailable = state.DirectStreamNativeMediaAvailable,
            NavigationRevision = state.NavigationRevision
        };
    }

    private RavaCastStatePayload BuildStatePayload()
    {
        if (_hosted is null) throw new InvalidOperationException("No hosted RavaCast");
        var ds = _surface.DirectStreamStatus;
        return new RavaCastStatePayload(_hosted.HostSessionId, _hosted.HostName, _hosted.CastName, _hosted.Url, _hosted.SourceDomain,
            _hosted.MediaTitle, _hosted.IsPlaying, Math.Max(0, _hosted.PositionSeconds), _hosted.DurationSeconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            _joinedViewers.Count, RavaCastPlanePayload.FromPlane(_hosted.Plane), _hosted.Queue.ToArray(), _hosted.ConsentCookies)
        {
            Mode = _hosted.Mode,
            PasswordProtected = _hosted.PasswordProtected,
            PasswordSalt = _hosted.PasswordSalt,
            DirectStreamQuality = _hosted.DirectStreamQuality,
            DirectStreamStatus = GetHostedDirectStreamStatus(ds),
            DirectStreamDetail = GetHostedDirectStreamDetail(ds),
            DirectStreamNativeMediaAvailable = ds.NativeMediaAvailable,
            NavigationRevision = _hosted.NavigationRevision
        };
    }

    private string GetHostedDirectStreamStatus(RavaCastDirectStreamBackendStatus ds)
    {
        if (_hosted is null) return ds.StatusText;
        if (_hosted.Mode != RavaCastMode.DirectStream)
            return string.IsNullOrWhiteSpace(_hosted.DirectStreamStatus) ? ds.StatusText : _hosted.DirectStreamStatus;
        if (!ds.PublisherActive && !ds.ReceiverActive && !string.IsNullOrWhiteSpace(_hosted.DirectStreamStatus))
            return _hosted.DirectStreamStatus;
        return ds.StatusText;
    }

    private string GetHostedDirectStreamDetail(RavaCastDirectStreamBackendStatus ds)
    {
        if (_hosted is null) return ds.Detail ?? string.Empty;
        if (_hosted.Mode != RavaCastMode.DirectStream)
            return string.IsNullOrWhiteSpace(_hosted.DirectStreamDetail) ? ds.Detail ?? string.Empty : _hosted.DirectStreamDetail;
        if (!ds.PublisherActive && !ds.ReceiverActive && !string.IsNullOrWhiteSpace(_hosted.DirectStreamDetail))
            return _hosted.DirectStreamDetail;
        return ds.Detail ?? string.Empty;
    }
}
