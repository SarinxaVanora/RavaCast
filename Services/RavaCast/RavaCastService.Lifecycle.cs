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

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var now = Environment.TickCount64;
        _lastPruneTick = now;
        _lastSurfaceSyncTick = now;
        _lastPlaybackRecoveryTick = now;
        _surface.DirectStreamSignalProduced -= OnDirectStreamSignalProduced;
        _surface.DirectStreamSignalProduced += OnDirectStreamSignalProduced;
        _framework.Update += FrameworkOnUpdate;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _framework.Update -= FrameworkOnUpdate;
        _surface.DirectStreamSignalProduced -= OnDirectStreamSignalProduced;
        _surface.StopDirectStreamPublisher();
        _surface.StopDirectStreamReceiver();
        _surface.Close();
        _activeCasts.Clear();
        _joinedViewers.Clear();
        _directStreamReadyViewers.Clear();
        CancelPendingHostedPlaneBroadcast();
        CancelPendingHostedBrowserNavigationSync();
        _pendingJoinMuted.Clear();
        _pendingPasswordProtectedJoinRetries.Clear();
        _directStreamSignalAssemblies.Clear();
        _completedDirectStreamSignalAssemblies.Clear();
        _directStreamSignalSendGates.Clear();
        return Task.CompletedTask;
    }

    private void FrameworkOnUpdate(IFramework framework)
    {
        if (_performanceCollector.Enabled)
            _performanceCollector.LogDiagnosticPerformance(this, "DirectFrameworkUpdate", () => FrameworkOnUpdateInternal(framework));
        else
            FrameworkOnUpdateInternal(framework);
    }

    private void FrameworkOnUpdateInternal(IFramework f)
    {
        var now = Environment.TickCount64;

        if (_hosted is not null && _pendingHostedPlaneFinalBroadcastTick > 0 && now >= _pendingHostedPlaneFinalBroadcastTick)
        {
            _pendingHostedPlaneFinalBroadcastTick = 0;
            _lastHostedPlaneBroadcastTick = now;
            BroadcastHostedStateNearby(RavaCastOp.StateSnapshot);
            BroadcastHostedStateToJoined();
        }

        if (_hosted is not null && _pendingHostedBrowserNavigationSyncTick > 0 && now >= _pendingHostedBrowserNavigationSyncTick)
        {
            _pendingHostedBrowserNavigationSyncTick = 0;
            SyncHostedUrlFromSurface();
        }

        if (_hosted is not null && now - _lastBroadcastTick >= BroadcastIntervalMs)
        {
            _lastBroadcastTick = now;
            BroadcastHostedStateNearby(RavaCastOp.Advertise);
        }

        if (now - _lastPruneTick >= 1000)
        {
            _lastPruneTick = now;
            foreach (var kv in _activeCasts.ToArray())
            {
                if (now - kv.Value.LastSeenTick > CastTtlMs)
                    _activeCasts.TryRemove(kv.Key, out _);
            }
        }

        if (now - _lastSurfaceSyncTick >= SurfaceSyncIntervalMs)
        {
            _lastSurfaceSyncTick = now;

            if (_hosted is { Mode: RavaCastMode.UrlShare })
            {
                SyncHostedObservedUrlFromSurface(now);
                SyncHostedMediaStateFromSurface();
                SyncHostedConsentCookiesFromSurface();
            }
        }

        if (now - _lastPlaybackRecoveryTick >= PlaybackRecoveryIntervalMs)
        {
            _lastPlaybackRecoveryTick = now;
            RecoverActivePlaybackSurface();
        }
    }

    public IReadOnlyList<RavaCastSummary> GetActiveCasts()
    {
        var mySession = GetMySessionId();
        return _activeCasts.Values
            .Where(c => !string.Equals(c.HostSessionId, mySession, StringComparison.Ordinal))
            .OrderBy(c => c.HostName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.CastName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public RavaCastSessionView? GetCurrentSession()
    {
        if (_hosted is not null)
        {
            var ds = _surface.DirectStreamStatus;
            return new RavaCastSessionView(_hosted.CastId, _hosted.HostSessionId, _hosted.HostName, _hosted.CastName, _hosted.Url,
                _hosted.SourceDomain, _hosted.MediaTitle, _hosted.IsPlaying, _hosted.PositionSeconds, _hosted.DurationSeconds,
                _hosted.StateUtc, _joinedViewers.Count, _hosted.Plane, _hosted.Queue.ToArray(), _hosted.ConsentCookies, true, _surface.Muted, _localVolume)
            {
                Mode = _hosted.Mode,
                PasswordProtected = _hosted.PasswordProtected,
                PasswordSalt = _hosted.PasswordSalt,
                DirectStreamQuality = _hosted.DirectStreamQuality,
                DirectStreamStatus = GetHostedDirectStreamStatus(ds),
                DirectStreamDetail = GetHostedDirectStreamDetail(ds),
                DirectStreamNativeMediaAvailable = ds.NativeMediaAvailable,
                DirectStreamConnectedPeers = ds.ConnectedPeerCount
            };
        }

        if (_joined is not null)
        {
            var ds = _surface.DirectStreamStatus;
            return new RavaCastSessionView(_joined.CastId, _joined.HostSessionId, _joined.HostName, _joined.CastName, _joined.Url,
                _joined.SourceDomain, _joined.MediaTitle, _joined.IsPlaying, _joined.PositionSeconds, _joined.DurationSeconds,
                _joined.StateUtc, _joined.JoinedCount, _joined.Plane, _joined.Queue, _joined.ConsentCookies, false, _joined.IsMuted, _joined.Volume)
            {
                Mode = _joined.Mode,
                PasswordProtected = _joined.PasswordProtected,
                PasswordSalt = _joined.PasswordSalt,
                DirectStreamQuality = _joined.DirectStreamQuality,
                DirectStreamStatus = _joined.Mode == RavaCastMode.DirectStream ? ds.StatusText : (string.IsNullOrWhiteSpace(_joined.DirectStreamStatus) ? ds.StatusText : _joined.DirectStreamStatus),
                DirectStreamDetail = _joined.Mode == RavaCastMode.DirectStream ? ds.Detail ?? string.Empty : (string.IsNullOrWhiteSpace(_joined.DirectStreamDetail) ? ds.Detail ?? string.Empty : _joined.DirectStreamDetail),
                DirectStreamNativeMediaAvailable = ds.NativeMediaAvailable,
                DirectStreamConnectedPeers = ds.ConnectedPeerCount
            };
        }

        return null;
    }

    public RavaCastRenderState? GetRenderState()
    {
        var current = GetCurrentSession();
        if (current is null) return null;
        return new RavaCastRenderState(current.CastId, current.CastName, current.SourceDomain, current.MediaTitle, current.Url,
            current.IsPlaying, current.PositionSeconds, current.DurationSeconds, current.Plane, current.IsOwner);
    }
}
