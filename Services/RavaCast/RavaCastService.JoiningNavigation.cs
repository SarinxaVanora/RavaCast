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

    public void Join(Guid castId, bool muted, string? passwordPlaintext)
    {
        if (!TryEnsurePlaybackBackendReady(out _)) return;
        if (!_activeCasts.TryGetValue(castId, out var summary)) return;
        var mySession = GetMySessionId();
        if (string.IsNullOrWhiteSpace(mySession)) return;

        _pendingJoinMuted[castId] = muted;
        string? passwordHash = null;
        if (summary.PasswordProtected)
            passwordHash = HashPassword(passwordPlaintext ?? string.Empty, summary.PasswordSalt);
        var payload = new RavaCastJoinPayload(mySession, _objects.LocalPlayer?.Name.TextValue ?? "Player", muted, passwordHash);
        SendEnvelope(mySession, summary.HostSessionId, new RavaCastEnvelope(castId, RavaCastOp.Join, MessagePackSerializer.Serialize(payload)));

        // Password-protected lobbies must wait for the host's accepted StateSnapshot before opening
        // the browser/Direct Stream receiver. Otherwise a wrong password would still let the viewer
        // open the advertised URL locally before the host can reject the join. Because that accepted
        // snapshot is an extra targeted mesh hop that normal lobbies do not need, retry the join request
        // briefly until the host accepts it so passworded lobbies do not feel like a dead button when one
        // packet arrives during a busy/object-table frame.
        if (summary.PasswordProtected)
        {
            _pendingPasswordProtectedJoinRetries[castId] = Environment.TickCount64;
            _ = RetryPasswordProtectedJoinUntilAcceptedAsync(castId, summary.HostSessionId, mySession, payload);
            return;
        }

        ApplyState(summary, muted, mySession);
        if (summary.Mode == RavaCastMode.DirectStream)
            StartJoinedDirectStreamReceiver(summary);
    }

    private async Task RetryPasswordProtectedJoinUntilAcceptedAsync(Guid castId, string hostSessionId, string mySession, RavaCastJoinPayload payload)
    {
        try
        {
            var token = _pendingPasswordProtectedJoinRetries.TryGetValue(castId, out var existingToken) ? existingToken : Environment.TickCount64;
            foreach (var delayMs in PasswordProtectedJoinRetryDelaysMs)
            {
                await Task.Delay(delayMs).ConfigureAwait(false);

                if (!_pendingPasswordProtectedJoinRetries.TryGetValue(castId, out var activeToken) || activeToken != token) return;
                if (!_pendingJoinMuted.ContainsKey(castId)) return;
                if (_joined is not null && _joined.CastId == castId) return;

                SendEnvelope(mySession, hostSessionId, new RavaCastEnvelope(castId, RavaCastOp.Join, MessagePackSerializer.Serialize(payload)));
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to retry RavaCast password-protected join request");
        }
    }

    public void RequestState()
    {
        if (_joined is null) return;
        var mySession = GetMySessionId();
        if (string.IsNullOrWhiteSpace(mySession)) return;
        SendEnvelope(mySession, _joined.HostSessionId, new RavaCastEnvelope(_joined.CastId, RavaCastOp.RequestState, null));
    }

    public void SyncHostedNavigationFromBrowserSoon(int delayMs = 650)
    {
        if (_hosted is null || _hosted.Mode != RavaCastMode.UrlShare) return;
        _pendingHostedBrowserNavigationSyncTick = Environment.TickCount64 + Math.Clamp(delayMs, 100, 2500);
    }

    public bool NavigateCurrentBrowserFromText(string value, out string error)
    {
        error = string.Empty;
        var target = NormaliseBrowserNavigationText(value);
        if (string.IsNullOrWhiteSpace(target))
        {
            error = "Enter a URL, domain, or search text.";
            return false;
        }

        if (!TryValidatePublicWebUrl(target, out var uri, out error))
            return false;

        if (_hosted is not null)
            return UpdateHostedUrl(uri.ToString(), out error);

        if (_joined is null)
        {
            error = "No active RavaCast browser.";
            return false;
        }

        if (_joined.Mode == RavaCastMode.DirectStream)
        {
            error = "Direct Stream viewers receive the host's browser; navigation stays with the host.";
            return false;
        }

        _joined.Url = uri.ToString();
        _joined.SourceDomain = uri.Host;
        _joined.MediaTitle = uri.Host;
        _joined.IsPlaying = true;
        _joined.PositionSeconds = 0;
        _joined.DurationSeconds = null;
        _joined.StateUtc = DateTime.UtcNow;
        _surface.Open(_joined.Url, _joined.IsMuted, _joined.Volume);
        return true;
    }

    public void GoBackCurrentBrowser()
    {
        _surface.GoBack();
        if (_hosted is not null)
            SyncHostedNavigationFromBrowserSoon();
    }

    public void GoForwardCurrentBrowser()
    {
        // IRavaCastTextureBackend only exposes Back/Reload directly at the moment. Use the
        // browser-standard Alt+Right accelerator so we do not need to change every backend just
        // to support the Current Cast Forward button.
        _surface.SendBrowserKey(39, true, null, shift: false, ctrl: false, alt: true);
        _surface.SendBrowserKey(39, false, null, shift: false, ctrl: false, alt: true);
        if (_hosted is not null)
            SyncHostedNavigationFromBrowserSoon();
    }

    public static string NormaliseBrowserNavigationText(string value)
    {
        var text = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        if (Uri.TryCreate(text, UriKind.Absolute, out var absolute) && !string.IsNullOrWhiteSpace(absolute.Scheme))
            return absolute.ToString();
        if (text.Contains(' ') || !text.Contains('.'))
            return "https://www.google.com/search?q=" + Uri.EscapeDataString(text);
        return "https://" + text.TrimStart('/');
    }

    private void RecoverActivePlaybackSurface()
    {
        if (_joined is not null)
        {
            if (_joined.Mode == RavaCastMode.UrlShare)
            {
                if (!_surface.IsOpen && !string.IsNullOrWhiteSpace(_joined.Url))
                {
                    _surface.Open(_joined.Url, _joined.IsMuted, _joined.Volume);
                    _surface.ApplyMediaState(_joined.PositionSeconds, _joined.IsPlaying, force: true);
                }
                return;
            }

            if (_joined.Mode == RavaCastMode.DirectStream)
            {
                var ds = _surface.DirectStreamStatus;
                if (ds.NativeMediaAvailable && (!_joined.DirectStreamReceiverRequested || !ds.ReceiverActive))
                {
                    var summary = BuildSummaryFromJoined(_joined);
                    _joined.DirectStreamReceiverRequested = false;
                    StartJoinedDirectStreamReceiver(summary);
                }
            }

            return;
        }

        if (_hosted is { Mode: RavaCastMode.UrlShare } hosted && !_surface.IsOpen && !string.IsNullOrWhiteSpace(hosted.Url))
        {
            _surface.Open(hosted.Url, muted: false, _localVolume);
            _surface.ApplyMediaState(hosted.PositionSeconds, hosted.IsPlaying, force: true);
        }
    }

    public void Leave()
    {
        if (_joined is not null)
        {
            var mySession = GetMySessionId();
            if (!string.IsNullOrWhiteSpace(mySession))
            {
                var payload = new RavaCastLeavePayload(mySession);
                SendEnvelope(mySession, _joined.HostSessionId, new RavaCastEnvelope(_joined.CastId, RavaCastOp.Leave, MessagePackSerializer.Serialize(payload)));
                SendEnvelope(mySession, _joined.HostSessionId, new RavaCastEnvelope(_joined.CastId, RavaCastOp.DirectStreamViewerLeft, MessagePackSerializer.Serialize(new RavaCastDirectStreamViewerPayload(mySession, _objects.LocalPlayer?.Name.TextValue ?? "Player"))));
            }
        }

        _surface.StopDirectStreamReceiver();
        _joined = null;
        foreach (var castId in _pendingJoinMuted.Keys.ToArray())
        {
            _pendingJoinMuted.TryRemove(castId, out _);
            _pendingPasswordProtectedJoinRetries.TryRemove(castId, out _);
        }
        _surface.Close();
    }

    public void SetLocalMuted(bool muted)
    {
        _surface.SetMuted(muted);
        if (_joined is not null) _joined.IsMuted = muted;
    }

    public void SetLocalVolume(float volume)
    {
        volume = NormaliseVolume(volume);
        if (Math.Abs(_localVolume - volume) < 0.001f) return;
        _localVolume = volume;
        _surface.SetVolume(volume);
        if (_joined is not null) _joined.Volume = volume;
    }

    public void PersistLocalVolume(float volume)
    {
        volume = NormaliseVolume(volume);
        if (Math.Abs(_config.Current.RavaCastDefaultVolume - volume) < 0.001f) return;
        _config.Current.RavaCastDefaultVolume = volume;
        _config.Save();
    }

    public void ReloadSurfaceForBrowserSettingsChange()
    {
        if (_hosted is not null)
        {
            var hosted = _hosted;
            var muted = _surface.Muted;
            var volume = _localVolume;

            StopHostedDirectStream("Browser settings changed.");
            _surface.Close();
            _surface.Open(hosted.Url, muted, volume);

            if (hosted.Mode == RavaCastMode.DirectStream)
                StartHostedDirectStreamPublisher(forceRestart: true);

            BroadcastHostedStateNearby(RavaCastOp.StateSnapshot);
            BroadcastHostedStateToJoined();
            return;
        }

        if (_joined is not null)
        {
            var joined = _joined;
            var summary = new RavaCastSummary(joined.CastId, joined.HostSessionId, joined.HostName, joined.CastName, joined.Url, joined.SourceDomain,
                joined.MediaTitle, joined.IsPlaying, joined.PositionSeconds, joined.DurationSeconds, joined.StateUtc, joined.JoinedCount,
                joined.Plane, joined.Queue, joined.ConsentCookies, Environment.TickCount64)
            {
                Mode = joined.Mode,
                PasswordProtected = joined.PasswordProtected,
                PasswordSalt = joined.PasswordSalt,
                DirectStreamQuality = joined.DirectStreamQuality,
                DirectStreamStatus = joined.DirectStreamStatus,
                DirectStreamDetail = joined.DirectStreamDetail
            };

            var muted = joined.IsMuted;
            var viewerSession = joined.ViewerSessionId;
            _surface.StopDirectStreamReceiver();
            _surface.Close();
            ApplyState(summary, muted, viewerSession);
        }
    }

    private bool TryEnsurePlaybackBackendReady(out string error)
    {
        var backend = _surface.BackendStatus;
        if (backend.IsAvailable)
        {
            error = string.Empty;
            return true;
        }

        error = string.IsNullOrWhiteSpace(backend.Detail)
            ? "RavaCast WebView2 renderer is missing from the plugin package. RavaCast.Renderer.exe must be bundled beside RavaCast.dll."
            : backend.Detail;
        return false;
    }

    private float GetDefaultVolume()
    {
        var raw = _config.Current.RavaCastDefaultVolume;
        var volume = raw <= 0.001f || float.IsNaN(raw) || float.IsInfinity(raw) ? 0.50f : NormaliseVolume(raw);
        if (Math.Abs(_config.Current.RavaCastDefaultVolume - volume) > 0.001f)
        {
            _config.Current.RavaCastDefaultVolume = volume;
            _config.Save();
        }
        return volume;
    }

    private static float NormaliseVolume(float volume)
    {
        if (float.IsNaN(volume) || float.IsInfinity(volume)) return 0.50f;
        return Math.Clamp(volume, 0.01f, 1f);
    }
}
