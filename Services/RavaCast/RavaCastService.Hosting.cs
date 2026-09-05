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

    public bool TryStartBroadcast(string castName, string url, RavaCastPlane plane, RavaCastMode mode, RavaCastDirectStreamQuality quality, string? password, out string error)
    {
        error = string.Empty;
        if (!TryValidatePublicWebUrl(url, out var uri, out error)) return false;
        if (!TryEnsurePlaybackBackendReady(out error)) return false;

        var hostSession = GetMySessionId();
        if (string.IsNullOrWhiteSpace(hostSession))
        {
            error = "RavaCast could not identify your current character session yet.";
            return false;
        }

        _surface.StopDirectStreamReceiver();
        _joined = null;
        _surface.Close();
        _joinedViewers.Clear();
        _directStreamReadyViewers.Clear();
        CancelPendingHostedPlaneBroadcast();
        CancelPendingHostedBrowserNavigationSync();
        ClearPendingHostedObservedUrlSync();

        var passwordSalt = string.Empty;
        var passwordHash = string.Empty;
        if (!string.IsNullOrWhiteSpace(password))
        {
            passwordSalt = Guid.NewGuid().ToString("N");
            passwordHash = HashPassword(password, passwordSalt);
        }

        _hosted = new HostedCast
        {
            HostSessionId = hostSession,
            HostName = _objects.LocalPlayer?.Name.TextValue ?? "Player",
            CastName = string.IsNullOrWhiteSpace(castName) ? "RavaCast" : castName.Trim(),
            Url = uri.ToString(),
            SourceDomain = uri.Host,
            MediaTitle = uri.Host,
            IsPlaying = true,
            PositionSeconds = 0,
            DurationSeconds = null,
            StateUtc = DateTime.UtcNow,
            Plane = plane,
            PasswordSalt = passwordSalt,
            PasswordHash = passwordHash,
            Mode = mode,
            DirectStreamQuality = quality
        };

        _surface.Open(_hosted.Url, muted: false, _localVolume);
        if (_hosted.Mode == RavaCastMode.DirectStream)
            StartHostedDirectStreamPublisher(forceRestart: true);
        BroadcastHostedStateNearby(RavaCastOp.Advertise);
        return true;
    }

    public void EndBroadcast()
    {
        if (_hosted is null) return;
        StopHostedDirectStream("Cast ended by owner.");
        var env = new RavaCastEnvelope(_hosted.CastId, RavaCastOp.ScreenClosed, null);
        SendEnvelopeNearby(_hosted.HostSessionId, env);
        foreach (var viewer in _joinedViewers.Keys.ToArray())
            SendEnvelope(_hosted.HostSessionId, viewer, env);
        RemoveDirectStreamSignalAssembliesForCast(_hosted.CastId);
        _hosted = null;
        _joinedViewers.Clear();
        _directStreamReadyViewers.Clear();
        CancelPendingHostedPlaneBroadcast();
        CancelPendingHostedBrowserNavigationSync();
        ClearPendingHostedObservedUrlSync();
        _surface.Close();
    }
    private void SyncHostedUrlFromSurface()
    {
        if (_hosted is null || _hosted.Mode != RavaCastMode.UrlShare) return;
        var currentUrl = _surface.CurrentUrl;
        if (string.IsNullOrWhiteSpace(currentUrl)) return;
        if (!TryValidatePublicWebUrl(currentUrl, out var uri, out _)) return;
        var nextUrl = uri.ToString();
        PromoteHostedObservedUrl(uri, nextUrl, immediateNavigation: true);
    }

    private void SyncHostedObservedUrlFromSurface(long now)
    {
        if (_hosted is null || _hosted.Mode != RavaCastMode.UrlShare) return;

        var media = _surface.CurrentMedia;
        var currentUrl = !string.IsNullOrWhiteSpace(media.Url) ? media.Url : _surface.CurrentUrl;
        if (string.IsNullOrWhiteSpace(currentUrl))
        {
            ClearPendingHostedObservedUrlSync();
            return;
        }

        if (!TryValidatePublicWebUrl(currentUrl, out var uri, out _))
        {
            ClearPendingHostedObservedUrlSync();
            return;
        }

        var nextUrl = uri.ToString();
        if (string.Equals(_hosted.Url, nextUrl, StringComparison.OrdinalIgnoreCase))
        {
            ClearPendingHostedObservedUrlSync();
            return;
        }

        if (!string.Equals(_pendingHostedObservedUrl, nextUrl, StringComparison.OrdinalIgnoreCase))
        {
            _pendingHostedObservedUrl = nextUrl;
            _pendingHostedObservedUrlSinceTick = now;
            return;
        }

        if (now - _pendingHostedObservedUrlSinceTick < HostedObservedUrlSyncDebounceMs) return;
        if (now - _lastHostedObservedUrlBroadcastTick < HostedObservedUrlSyncMinimumIntervalMs) return;

        PromoteHostedObservedUrl(uri, nextUrl, immediateNavigation: false);
    }

    private void PromoteHostedObservedUrl(Uri uri, string nextUrl, bool immediateNavigation)
    {
        if (_hosted is null || _hosted.Mode != RavaCastMode.UrlShare) return;
        if (string.Equals(_hosted.Url, nextUrl, StringComparison.OrdinalIgnoreCase))
        {
            ClearPendingHostedObservedUrlSync();
            return;
        }

        var media = _surface.CurrentMedia;
        _hosted.Url = nextUrl;
        _hosted.SourceDomain = uri.Host;
        _hosted.MediaTitle = !string.IsNullOrWhiteSpace(media.Title) ? media.Title : uri.Host;
        _hosted.IsPlaying = immediateNavigation ? true : media.IsPlaying;
        _hosted.PositionSeconds = immediateNavigation ? 0 : Math.Max(0, media.PositionSeconds);
        _hosted.DurationSeconds = immediateNavigation ? null : (media.DurationSeconds is > 0 ? media.DurationSeconds : null);
        _hosted.StateUtc = immediateNavigation || media.StateUtc == default ? DateTime.UtcNow : media.StateUtc;
        _hosted.NavigationRevision++;
        _lastHostedObservedUrlBroadcastTick = Environment.TickCount64;
        ClearPendingHostedObservedUrlSync();
        BroadcastHostedNavigationStateReliably();
    }

    private void SyncHostedMediaStateFromSurface()
    {
        if (_hosted is null || _hosted.Mode != RavaCastMode.UrlShare) return;
        var media = _surface.CurrentMedia;
        var title = !string.IsNullOrWhiteSpace(media.Title) ? media.Title : _hosted.SourceDomain;
        var position = Math.Max(0, media.PositionSeconds);
        var duration = media.DurationSeconds is > 0 ? media.DurationSeconds : null;
        var changed = Math.Abs(_hosted.PositionSeconds - position) >= 0.75
            || _hosted.IsPlaying != media.IsPlaying
            || Math.Abs((_hosted.DurationSeconds ?? -1) - (duration ?? -1)) >= 0.75
            || (!string.IsNullOrWhiteSpace(title) && !string.Equals(_hosted.MediaTitle, title, StringComparison.Ordinal));

        if (!changed) return;
        _hosted.MediaTitle = title;
        _hosted.IsPlaying = media.IsPlaying;
        _hosted.PositionSeconds = position;
        _hosted.DurationSeconds = duration;
        _hosted.StateUtc = media.StateUtc == default ? DateTime.UtcNow : media.StateUtc;
        BroadcastHostedStateNearby(RavaCastOp.StateSnapshot);
        BroadcastHostedStateToJoined();
    }

    private void SyncHostedConsentCookiesFromSurface()
    {
        if (_hosted is null) return;
        var cookies = _surface.GetShareableConsentCookies();
        if (ConsentCookiesEqual(_hosted.ConsentCookies, cookies)) return;
        _hosted.ConsentCookies = cookies;
        BroadcastHostedConsentCookies();
    }

    private void BroadcastHostedConsentCookies()
    {
        if (_hosted is null || _hosted.ConsentCookies.Length == 0) return;
        var payload = MessagePackSerializer.Serialize(_hosted.ConsentCookies);
        var env = new RavaCastEnvelope(_hosted.CastId, RavaCastOp.ConsentCookies, payload);
        SendEnvelopeNearby(_hosted.HostSessionId, env);
        foreach (var viewer in _joinedViewers.Keys.ToArray())
            SendEnvelope(_hosted.HostSessionId, viewer, env);
    }

    private void HandleConsentCookies(RavaCastEnvelope env)
    {
        if (env.Payload is null) return;
        var cookies = MessagePackSerializer.Deserialize<RavaCastCookiePayload[]>(env.Payload) ?? [];
        if (_joined is not null && _joined.CastId == env.CastId)
        {
            _joined.ConsentCookies = cookies;
            _surface.ApplySharedConsentCookies(_joined.Url, cookies);
        }
        if (_activeCasts.TryGetValue(env.CastId, out var summary))
            _activeCasts[env.CastId] = summary with { ConsentCookies = cookies };
    }

    private static bool ConsentCookiesEqual(IReadOnlyList<RavaCastCookiePayload>? left, IReadOnlyList<RavaCastCookiePayload>? right)
    {
        left ??= [];
        right ??= [];
        if (left.Count != right.Count) return false;
        for (var i = 0; i < left.Count; i++)
        {
            var a = left[i];
            var b = right[i];
            if (!string.Equals(a.Name, b.Name, StringComparison.Ordinal)
                || !string.Equals(a.Value, b.Value, StringComparison.Ordinal)
                || !string.Equals(a.Domain, b.Domain, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(a.Path, b.Path, StringComparison.Ordinal)
                || a.ExpiresUnixMs != b.ExpiresUnixMs
                || a.Secure != b.Secure
                || !string.Equals(a.SameSite, b.SameSite, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }


    public bool UpdateHostedUrl(string url, out string error)
    {
        error = string.Empty;
        if (_hosted is null)
        {
            error = "No active RavaCast broadcast.";
            return false;
        }

        if (!TryValidatePublicWebUrl(url, out var uri, out error)) return false;

        _hosted.Url = uri.ToString();
        _hosted.SourceDomain = uri.Host;
        _hosted.MediaTitle = uri.Host;
        _hosted.IsPlaying = true;
        _hosted.PositionSeconds = 0;
        _hosted.DurationSeconds = null;
        _hosted.StateUtc = DateTime.UtcNow;
        _hosted.NavigationRevision++;
        ClearPendingHostedObservedUrlSync();
        _surface.Open(_hosted.Url, muted: _surface.Muted, _localVolume);
        BroadcastHostedNavigationStateReliably();
        return true;
    }

    public void UpdateHostedPlane(RavaCastPlane plane, bool broadcast = true)
    {
        if (_hosted is null) return;
        _hosted.Plane = plane;

        if (broadcast)
        {
            BroadcastHostedPlaneState();
            return;
        }

        ScheduleHostedPlaneFinalBroadcast();
    }

    private void BroadcastHostedPlaneState()
    {
        if (_hosted is null) return;
        CancelPendingHostedPlaneBroadcast();
        _lastHostedPlaneBroadcastTick = Environment.TickCount64;
        BroadcastHostedStateNearby(RavaCastOp.StateSnapshot);
        BroadcastHostedStateToJoined();
    }

    private void ScheduleHostedPlaneFinalBroadcast()
    {
        if (_hosted is null) return;
        _pendingHostedPlaneFinalBroadcastTick = Environment.TickCount64 + LivePlaneFinalDebounceMs;
    }

    private void ClearPendingHostedObservedUrlSync()
    {
        _pendingHostedObservedUrl = string.Empty;
        _pendingHostedObservedUrlSinceTick = 0;
    }

    public bool ShouldBroadcastHostedPlaneNow(bool force)
    {
        if (force) return true;
        var now = Environment.TickCount64;
        return now - _lastHostedPlaneBroadcastTick >= LivePlaneBroadcastIntervalMs;
    }


    public void SetHostedMode(RavaCastMode mode)
    {
        if (_hosted is null || _hosted.Mode == mode) return;
        _hosted.Mode = mode;
        if (mode == RavaCastMode.DirectStream)
            StartHostedDirectStreamPublisher();
        else
            StopHostedDirectStream("Direct Stream switched back to URL Share.");
        BroadcastHostedStateNearby(RavaCastOp.StateSnapshot);
        BroadcastHostedStateToJoined();
    }

    public void SetHostedDirectStreamQuality(RavaCastDirectStreamQuality quality)
    {
        if (_hosted is null || _hosted.DirectStreamQuality == quality) return;
        _hosted.DirectStreamQuality = quality;
        if (_hosted.Mode == RavaCastMode.DirectStream)
            StartHostedDirectStreamPublisher(forceRestart: true);
        BroadcastHostedStateNearby(RavaCastOp.StateSnapshot);
        BroadcastHostedStateToJoined();
    }

    public void SetHostedPlayback(bool playing)
    {
        if (_hosted is null || _hosted.Mode != RavaCastMode.UrlShare) return;
        _hosted.IsPlaying = playing;
        _hosted.StateUtc = DateTime.UtcNow;
        _surface.ApplyMediaState(_hosted.PositionSeconds, playing, force: true);
        BroadcastHostedStateNearby(RavaCastOp.StateSnapshot);
        BroadcastHostedStateToJoined();
    }

    public void SeekHosted(double positionSeconds)
    {
        if (_hosted is null || _hosted.Mode != RavaCastMode.UrlShare) return;
        _hosted.PositionSeconds = Math.Max(0, positionSeconds);
        _hosted.StateUtc = DateTime.UtcNow;
        _surface.ApplyMediaState(_hosted.PositionSeconds, _hosted.IsPlaying, force: true);
        BroadcastHostedStateNearby(RavaCastOp.StateSnapshot);
        BroadcastHostedStateToJoined();
    }

    public bool QueueHostedUrl(string url, out string error)
    {
        error = string.Empty;
        if (_hosted is null)
        {
            error = "No active RavaCast broadcast.";
            return false;
        }

        if (!TryValidatePublicWebUrl(url, out var uri, out error)) return false;
        _hosted.Queue.Add(uri.ToString());
        BroadcastHostedStateNearby(RavaCastOp.StateSnapshot);
        BroadcastHostedStateToJoined();
        return true;
    }

    public void ClearHostedQueue()
    {
        if (_hosted is null) return;
        _hosted.Queue.Clear();
        BroadcastHostedStateNearby(RavaCastOp.StateSnapshot);
        BroadcastHostedStateToJoined();
    }

    public bool PlayNextQueued(out string error)
    {
        error = string.Empty;
        if (_hosted is null)
        {
            error = "No active RavaCast broadcast.";
            return false;
        }

        if (_hosted.Queue.Count == 0)
        {
            error = "The RavaCast queue is empty.";
            return false;
        }

        var next = _hosted.Queue[0];
        _hosted.Queue.RemoveAt(0);
        return UpdateHostedUrl(next, out error);
    }
}
