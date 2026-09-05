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


    private void BroadcastHostedNavigationStateReliably()
    {
        if (_hosted is null) return;
        _lastBroadcastTick = Environment.TickCount64;
        var castId = _hosted.CastId;
        var navigationRevision = _hosted.NavigationRevision;
        BroadcastHostedStateNearby(RavaCastOp.StateSnapshot);
        BroadcastHostedStateToJoined();
        _ = RepeatHostedNavigationStateAsync(castId, navigationRevision);
    }

    private async Task RepeatHostedNavigationStateAsync(Guid castId, long navigationRevision)
    {
        try
        {
            foreach (var delayMs in new[] { 300, 900, 1800 })
            {
                await Task.Delay(delayMs).ConfigureAwait(false);
                if (_hosted is null || _hosted.CastId != castId || _hosted.NavigationRevision != navigationRevision) return;
                BroadcastHostedStateNearby(RavaCastOp.StateSnapshot);
                BroadcastHostedStateToJoined();
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to repeat RavaCast navigation state");
        }
    }

    private void BroadcastHostedStateNearby(RavaCastOp op)
    {
        if (_hosted is null) return;
        var env = new RavaCastEnvelope(_hosted.CastId, op, MessagePackSerializer.Serialize(BuildStatePayload()));
        SendEnvelopeNearby(_hosted.HostSessionId, env);
    }

    private void BroadcastHostedStateToJoined()
    {
        if (_hosted is null) return;
        foreach (var viewer in _joinedViewers.Keys.ToArray())
            SendHostedState(viewer, RavaCastOp.StateSnapshot);
    }

    private void SendHostedState(string targetSessionId, RavaCastOp op)
    {
        if (_hosted is null || string.IsNullOrWhiteSpace(targetSessionId)) return;
        var env = new RavaCastEnvelope(_hosted.CastId, op, MessagePackSerializer.Serialize(BuildStatePayload()));
        SendEnvelope(_hosted.HostSessionId, targetSessionId, env);
    }


    private void StartHostedDirectStreamPublisher(bool notifyExistingViewers = true, bool forceRestart = false)
    {
        if (_hosted is null) return;

        var viewers = _joinedViewers.Keys.ToArray();
        if (viewers.Length == 0)
        {
            // Do not spin up BridgeHost/libdatachannel/FFmpeg just because the owner selected Direct Stream.
            // The heavy media path starts lazily when the first viewer actually joins. This keeps selecting
            // Direct Stream essentially free for the game and avoids the constant host-side hitches reported
            // while the stream is only being prepared.
            _hosted.DirectStreamPublisherRequested = false;
            _hosted.DirectStreamStatus = "Ready — waiting for viewers";
            _hosted.DirectStreamDetail = "Direct Stream will start when someone joins.";
            return;
        }

        var status = _surface.DirectStreamStatus;
        if (!status.PublisherActive && DirectStreamStatusMeansStoppedOrFailed(status))
            _hosted.DirectStreamPublisherRequested = false;

        if ((status.PublisherActive || _hosted.DirectStreamPublisherRequested) && !forceRestart)
        {
            if (notifyExistingViewers)
                foreach (var viewer in viewers)
                    SendDirectStreamStart(viewer);
            return;
        }

        if ((status.PublisherActive || _hosted.DirectStreamPublisherRequested) && forceRestart)
        {
            _surface.StopDirectStreamPublisher();
            _directStreamReadyViewers.Clear();
            _hosted.DirectStreamPublisherRequested = false;
        }

        _hosted.DirectStreamPublisherRequested = true;
        if (_surface.StartDirectStreamPublisher(_hosted.CastId, _hosted.DirectStreamQuality, out var error))
        {
            _hosted.DirectStreamStatus = "Starting Direct Stream";
            _hosted.DirectStreamDetail = string.Empty;
            if (notifyExistingViewers)
                foreach (var viewer in viewers)
                    SendDirectStreamStart(viewer);
            return;
        }

        // Direct Stream is an explicit mode. Do not silently downgrade to URL Share when the media bridge
        // fails; that hides the real problem and can make viewers think they are testing Direct Stream when
        // they are only seeing their local URL-share browser. Keep the cast in Direct Stream and surface the
        // bridge/startup error clearly.
        _hosted.DirectStreamPublisherRequested = false;
        _surface.StopDirectStreamPublisher();
        _hosted.DirectStreamStatus = "Direct Stream failed";
        _hosted.DirectStreamDetail = error;
        _logger.LogWarning("RavaCast Direct Stream publisher could not start: {error}", error);
    }

    private static bool DirectStreamStatusMeansStoppedOrFailed(RavaCastDirectStreamBackendStatus status)
    {
        var text = status.StatusText ?? string.Empty;
        var detail = status.Detail ?? string.Empty;
        return text.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("error", StringComparison.OrdinalIgnoreCase)
            || text.Contains("stopped", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("error", StringComparison.OrdinalIgnoreCase);
    }

    private void StopHostedDirectStreamIfNoViewers()
    {
        if (_hosted is null || _hosted.Mode != RavaCastMode.DirectStream || !_joinedViewers.IsEmpty) return;
        _surface.StopDirectStreamPublisher();
        _directStreamReadyViewers.Clear();
        _hosted.DirectStreamPublisherRequested = false;
        _hosted.DirectStreamStatus = "Ready — waiting for viewers";
        _hosted.DirectStreamDetail = "Direct Stream will restart when another viewer joins.";
    }

    private void StopHostedDirectStream(string reason)
    {
        if (_hosted is null) return;
        var payload = MessagePackSerializer.Serialize(new RavaCastDirectStreamStopPayload(reason));
        var env = new RavaCastEnvelope(_hosted.CastId, RavaCastOp.DirectStreamStop, payload);
        foreach (var viewer in _joinedViewers.Keys.ToArray())
            SendEnvelope(_hosted.HostSessionId, viewer, env);
        _surface.StopDirectStreamPublisher();
        _directStreamReadyViewers.Clear();
        _hosted.DirectStreamPublisherRequested = false;
        _hosted.DirectStreamStatus = "Direct Stream stopped";
        _hosted.DirectStreamDetail = reason;
    }

    private void SendDirectStreamStart(string viewerSessionId)
    {
        if (_hosted is null || string.IsNullOrWhiteSpace(viewerSessionId)) return;
        var payload = new RavaCastDirectStreamStartPayload(_hosted.HostSessionId, _hosted.DirectStreamQuality);
        SendEnvelope(_hosted.HostSessionId, viewerSessionId, new RavaCastEnvelope(_hosted.CastId, RavaCastOp.DirectStreamStart, MessagePackSerializer.Serialize(payload)));
    }

    private void StartJoinedDirectStreamReceiver(RavaCastSummary summary)
    {
        if (_joined is null || _joined.CastId != summary.CastId) return;
        var mySession = !string.IsNullOrWhiteSpace(_joined.ViewerSessionId) ? _joined.ViewerSessionId : GetMySessionId();
        if (string.IsNullOrWhiteSpace(mySession)) return;
        _joined.ViewerSessionId = mySession;

        var ds = _surface.DirectStreamStatus;
        if (_joined.DirectStreamReceiverRequested && ds.ReceiverActive)
            return;

        _joined.DirectStreamReceiverRequested = true;
        if (_surface.StartDirectStreamReceiver(summary.CastId, summary.HostSessionId, mySession, summary.DirectStreamQuality, out var error))
        {
            _joined.DirectStreamStatus = "Connecting to host video";
            _joined.DirectStreamDetail = string.Empty;
            SendDirectStreamViewerReady(summary.HostSessionId, summary.CastId, mySession);
            _ = SendDirectStreamViewerReadyRetryAsync(summary.HostSessionId, summary.CastId, mySession);
            return;
        }

        _joined.DirectStreamReceiverRequested = false;
        _joined.DirectStreamStatus = "Could not connect to host video";
        _joined.DirectStreamDetail = error;
        SendDirectStreamError(summary.HostSessionId, summary.CastId, error);
    }

    private void SendDirectStreamViewerReady(string hostSessionId, Guid castId, string mySession)
    {
        if (string.IsNullOrWhiteSpace(hostSessionId) || string.IsNullOrWhiteSpace(mySession)) return;
        RunOnFrameworkThreadSafe(() => SendDirectStreamViewerReadyOnFramework(hostSessionId, castId, mySession), "Direct Stream viewer ready");
    }

    private void SendDirectStreamViewerReadyOnFramework(string hostSessionId, Guid castId, string mySession)
    {
        var ready = new RavaCastDirectStreamViewerPayload(mySession, _objects.LocalPlayer?.Name.TextValue ?? "Player");
        SendEnvelope(mySession, hostSessionId, new RavaCastEnvelope(castId, RavaCastOp.DirectStreamViewerReady, MessagePackSerializer.Serialize(ready)));
    }

    private async Task SendDirectStreamViewerReadyRetryAsync(string hostSessionId, Guid castId, string mySession)
    {
        try
        {
            var delays = new[] { 250, 1000, 2500, 5000, 8000 };
            foreach (var delay in delays)
            {
                await Task.Delay(delay).ConfigureAwait(false);

                if (_joined is null || _joined.CastId != castId || !_joined.DirectStreamReceiverRequested) return;
                SendDirectStreamViewerReady(hostSessionId, castId, mySession);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to retry RavaCast Direct Stream viewer ready message");
        }
    }

    private void HandleDirectStreamStart(string fromSessionId, RavaCastEnvelope env)
    {
        if (_joined is null || _joined.CastId != env.CastId || env.Payload is null) return;
        if (!string.Equals(_joined.HostSessionId, fromSessionId, StringComparison.Ordinal)) return;
        var payload = MessagePackSerializer.Deserialize<RavaCastDirectStreamStartPayload>(env.Payload);
        var summary = new RavaCastSummary(_joined.CastId, _joined.HostSessionId, _joined.HostName, _joined.CastName, _joined.Url, _joined.SourceDomain,
            _joined.MediaTitle, true, 0, null, _joined.StateUtc, _joined.JoinedCount,
            _joined.Plane, _joined.Queue, _joined.ConsentCookies, Environment.TickCount64)
        {
            Mode = RavaCastMode.DirectStream,
            DirectStreamQuality = payload.Quality
        };
        StartJoinedDirectStreamReceiver(summary);
    }

    private void HandleDirectStreamStop(RavaCastEnvelope env)
    {
        if (_joined is null || _joined.CastId != env.CastId) return;
        var reason = "Direct Stream stopped by host.";
        if (env.Payload is not null)
        {
            try { reason = MessagePackSerializer.Deserialize<RavaCastDirectStreamStopPayload>(env.Payload).Reason; } catch { }
        }
        _surface.StopDirectStreamReceiver();
        _joined.DirectStreamReceiverRequested = false;
        _joined.DirectStreamStatus = "Direct Stream stopped";
        _joined.DirectStreamDetail = reason;
    }

    private void HandleDirectStreamViewerReady(string fromSessionId, RavaCastEnvelope env)
    {
        if (_hosted is null || _hosted.CastId != env.CastId || env.Payload is null) return;
        var payload = MessagePackSerializer.Deserialize<RavaCastDirectStreamViewerPayload>(env.Payload);
        var viewer = !string.IsNullOrWhiteSpace(payload.ViewerSessionId) ? payload.ViewerSessionId : fromSessionId;
        if (string.IsNullOrWhiteSpace(viewer)) return;

        // Treat the first media-ready message as a valid joined-viewer heartbeat too. Do not process
        // repeated ready heartbeats as new peers: moving/resizing the live screen sends state updates,
        // and older builds answered every state update with another ViewerReady. That created a feedback
        // loop of AddPeer/StateSnapshot traffic and could tank Direct Stream frame rate while placing.
        _joinedViewers[viewer] = true;
        if (!_directStreamReadyViewers.TryAdd(viewer, true))
            return;

        StartHostedDirectStreamPublisher(notifyExistingViewers: false);
        _surface.AddDirectStreamPeer(viewer);
        _hosted.DirectStreamStatus = "Viewer connected";
        _hosted.DirectStreamDetail = string.IsNullOrWhiteSpace(payload.ViewerName) ? "A viewer is connecting to your stream." : $"{payload.ViewerName} is connecting to your stream.";
        BroadcastHostedStateNearby(RavaCastOp.Advertise);
        BroadcastHostedStateToJoined();
    }

    private void HandleDirectStreamViewerLeft(string fromSessionId, RavaCastEnvelope env)
    {
        if (_hosted is null || _hosted.CastId != env.CastId) return;
        var viewer = fromSessionId;
        if (env.Payload is not null)
        {
            try
            {
                var payload = MessagePackSerializer.Deserialize<RavaCastDirectStreamViewerPayload>(env.Payload);
                if (!string.IsNullOrWhiteSpace(payload.ViewerSessionId)) viewer = payload.ViewerSessionId;
            }
            catch { }
        }
        if (!string.IsNullOrWhiteSpace(viewer))
        {
            _joinedViewers.TryRemove(viewer, out _);
            _directStreamReadyViewers.TryRemove(viewer, out _);
            _surface.RemoveDirectStreamPeer(viewer);
            StopHostedDirectStreamIfNoViewers();
        }
    }

    private void HandleDirectStreamSignal(string fromSessionId, RavaCastEnvelope env)
    {
        if (env.Payload is null) return;
        var payload = MessagePackSerializer.Deserialize<RavaCastDirectStreamSignalPayload>(env.Payload);
        ProcessDirectStreamSignalPayload(fromSessionId, env.CastId, payload);
    }

    private void HandleDirectStreamSignalChunk(string fromSessionId, RavaCastEnvelope env)
    {
        if (env.Payload is null) return;
        var chunk = MessagePackSerializer.Deserialize<RavaCastDirectStreamSignalChunkPayload>(env.Payload);
        if (string.IsNullOrWhiteSpace(chunk.SignalId) || string.IsNullOrWhiteSpace(chunk.Type)) return;
        if (chunk.ChunkCount <= 0 || chunk.ChunkCount > 256) return;
        if (chunk.ChunkIndex < 0 || chunk.ChunkIndex >= chunk.ChunkCount) return;

        var signalFrom = !string.IsNullOrWhiteSpace(chunk.FromSessionId) ? chunk.FromSessionId : fromSessionId;
        var signalTo = !string.IsNullOrWhiteSpace(chunk.ToSessionId) ? chunk.ToSessionId : GetExpectedDirectStreamLocalSessionId();
        if (!IsDirectStreamSignalForThisClient(signalTo))
            return;

        PruneDirectStreamSignalAssemblies();
        var key = BuildDirectStreamSignalAssemblyKey(env.CastId, chunk.SignalId, signalFrom, signalTo, chunk.Type);
        if (_completedDirectStreamSignalAssemblies.ContainsKey(key)) return;
        var assembly = _directStreamSignalAssemblies.GetOrAdd(key, _ => new DirectStreamSignalAssembly(env.CastId, chunk.SignalId, signalFrom, signalTo, chunk.Type, chunk.ChunkCount));

        bool complete;
        lock (assembly)
            complete = assembly.TryAdd(chunk.ChunkIndex, chunk.PayloadPart);

        if (!complete) return;
        _directStreamSignalAssemblies.TryRemove(key, out _);
        _completedDirectStreamSignalAssemblies[key] = Environment.TickCount64;
        var payload = new RavaCastDirectStreamSignalPayload(signalFrom, signalTo, chunk.Type, assembly.BuildPayloadJson());
        ProcessDirectStreamSignalPayload(fromSessionId, env.CastId, payload);
    }

    private void ProcessDirectStreamSignalPayload(string fromSessionId, Guid castId, RavaCastDirectStreamSignalPayload payload)
    {
        var signalFrom = !string.IsNullOrWhiteSpace(payload.FromSessionId) ? payload.FromSessionId : fromSessionId;
        var signalTo = payload.ToSessionId ?? string.Empty;
        if (!IsDirectStreamSignalForThisClient(signalTo))
            return;

        if (_hosted is not null && _hosted.CastId == castId)
        {
            var peerId = ResolveHostedDirectStreamPeer(signalFrom, fromSessionId);
            if (string.IsNullOrWhiteSpace(peerId)) return;
            _surface.HandleDirectStreamSignal(peerId, payload.Type, payload.PayloadJson);
            return;
        }

        if (_joined is not null && _joined.CastId == castId && IsJoinedDirectStreamHost(signalFrom, fromSessionId))
            _surface.HandleDirectStreamSignal(_joined.HostSessionId, payload.Type, payload.PayloadJson);
    }

    private bool IsDirectStreamSignalForThisClient(string signalTo)
    {
        if (string.IsNullOrWhiteSpace(signalTo)) return true;

        var mySession = GetMySessionId();
        if (!string.IsNullOrWhiteSpace(mySession) && string.Equals(signalTo, mySession, StringComparison.Ordinal)) return true;
        if (_hosted is not null && string.Equals(signalTo, _hosted.HostSessionId, StringComparison.Ordinal)) return true;
        if (_joined is not null && !string.IsNullOrWhiteSpace(_joined.ViewerSessionId) && string.Equals(signalTo, _joined.ViewerSessionId, StringComparison.Ordinal)) return true;
        return false;
    }

    private string GetExpectedDirectStreamLocalSessionId()
    {
        if (_hosted is not null && !string.IsNullOrWhiteSpace(_hosted.HostSessionId)) return _hosted.HostSessionId;
        if (_joined is not null && !string.IsNullOrWhiteSpace(_joined.ViewerSessionId)) return _joined.ViewerSessionId;
        return GetMySessionId();
    }

    private string ResolveHostedDirectStreamPeer(string signalFrom, string meshFromSessionId)
    {
        if (!string.IsNullOrWhiteSpace(signalFrom) && _joinedViewers.ContainsKey(signalFrom)) return signalFrom;
        if (!string.IsNullOrWhiteSpace(meshFromSessionId) && _joinedViewers.ContainsKey(meshFromSessionId)) return meshFromSessionId;
        return string.Empty;
    }

    private bool IsJoinedDirectStreamHost(string signalFrom, string meshFromSessionId)
    {
        if (_joined is null) return false;
        if (!string.IsNullOrWhiteSpace(signalFrom) && string.Equals(_joined.HostSessionId, signalFrom, StringComparison.Ordinal)) return true;
        if (!string.IsNullOrWhiteSpace(meshFromSessionId) && string.Equals(_joined.HostSessionId, meshFromSessionId, StringComparison.Ordinal)) return true;
        return false;
    }

    private void PruneDirectStreamSignalAssemblies()
    {
        var now = Environment.TickCount64;
        foreach (var kv in _directStreamSignalAssemblies.ToArray())
            if (now - kv.Value.CreatedTick > DirectStreamSignalAssemblyTtlMs)
                _directStreamSignalAssemblies.TryRemove(kv.Key, out _);

        foreach (var kv in _completedDirectStreamSignalAssemblies.ToArray())
            if (now - kv.Value > DirectStreamSignalAssemblyTtlMs)
                _completedDirectStreamSignalAssemblies.TryRemove(kv.Key, out _);
    }

    private void RemoveDirectStreamSignalAssembliesForCast(Guid castId)
    {
        foreach (var kv in _directStreamSignalAssemblies.ToArray())
            if (kv.Value.CastId == castId)
                _directStreamSignalAssemblies.TryRemove(kv.Key, out _);

        var prefix = castId.ToString("D") + "|";
        foreach (var kv in _completedDirectStreamSignalAssemblies.ToArray())
            if (kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                _completedDirectStreamSignalAssemblies.TryRemove(kv.Key, out _);
    }

    private void HandleDirectStreamStats(RavaCastEnvelope env)
    {
        if (env.Payload is null) return;
        try
        {
            _ = MessagePackSerializer.Deserialize<RavaCastDirectStreamStatsPayload>(env.Payload);
            // Direct Stream stats are healthy-path telemetry; do not log them by default.
        }
        catch { }
    }

    private void HandleDirectStreamError(string fromSessionId, RavaCastEnvelope env)
    {
        if (env.Payload is null) return;
        try
        {
            var payload = MessagePackSerializer.Deserialize<RavaCastDirectStreamErrorPayload>(env.Payload);
            _logger.LogWarning("RavaCast Direct Stream error from {session}: {message}", string.IsNullOrWhiteSpace(payload.SessionId) ? fromSessionId : payload.SessionId, payload.Message);
            if (_hosted is not null && _hosted.CastId == env.CastId)
            {
                _hosted.DirectStreamStatus = "Direct Stream viewer error";
                _hosted.DirectStreamDetail = payload.Message;
            }
            if (_joined is not null && _joined.CastId == env.CastId)
            {
                _joined.DirectStreamStatus = "Direct Stream error";
                _joined.DirectStreamDetail = payload.Message;
            }
        }
        catch { }
    }

    private void OnDirectStreamSignalProduced(object? sender, RavaCastDirectStreamSignalProducedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.PeerId)) return;

        Guid castId;
        string localSessionId;
        if (_hosted is not null)
        {
            castId = _hosted.CastId;
            localSessionId = _hosted.HostSessionId;
        }
        else if (_joined is not null)
        {
            castId = _joined.CastId;
            localSessionId = !string.IsNullOrWhiteSpace(_joined.ViewerSessionId) ? _joined.ViewerSessionId : GetMySessionId();
            if (!string.IsNullOrWhiteSpace(localSessionId)) _joined.ViewerSessionId = localSessionId;
        }
        else
        {
            return;
        }

        if (castId == Guid.Empty || string.IsNullOrWhiteSpace(localSessionId)) return;
        SendDirectStreamSignal(localSessionId, e.PeerId, castId, e.SignalType, e.PayloadJson);
    }

    private async Task SendDirectStreamSignalAsync(string fromSessionId, string targetSessionId, Guid castId, string signalType, string payloadJson)
    {
        var gateKey = $"{castId:D}|{fromSessionId}|{targetSessionId}";
        var gate = _directStreamSignalSendGates.GetOrAdd(gateKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ShouldChunkDirectStreamSignal(signalType, payloadJson))
            {
                await SendDirectStreamSignalChunksAsync(fromSessionId, targetSessionId, castId, signalType, payloadJson ?? string.Empty).ConfigureAwait(false);
                return;
            }

            var op = DirectStreamSignalOp(signalType);
            var payload = new RavaCastDirectStreamSignalPayload(fromSessionId, targetSessionId, signalType, payloadJson ?? string.Empty);
            await SendEnvelopeAsync(fromSessionId, targetSessionId, new RavaCastEnvelope(castId, op, MessagePackSerializer.Serialize(payload))).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send RavaCast Direct Stream signal {type} to {targetSessionId}", signalType, targetSessionId);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task SendDirectStreamSignalChunksAsync(string fromSessionId, string targetSessionId, Guid castId, string signalType, string payloadJson)
    {
        var signalId = Guid.NewGuid().ToString("N");
        var safePayload = payloadJson ?? string.Empty;
        var count = Math.Max(1, (safePayload.Length + DirectStreamSignalChunkChars - 1) / DirectStreamSignalChunkChars);
        var repeatCount = IsDirectStreamDescriptionSignal(signalType) ? 2 : 1;

        for (var pass = 0; pass < repeatCount; pass++)
        {
            if (pass > 0)
            {
                try { await Task.Delay(180).ConfigureAwait(false); }
                catch { return; }
            }

            for (var i = 0; i < count; i++)
            {
                var offset = i * DirectStreamSignalChunkChars;
                var length = Math.Min(DirectStreamSignalChunkChars, Math.Max(0, safePayload.Length - offset));
                var part = length > 0 ? safePayload.Substring(offset, length) : string.Empty;
                var chunk = new RavaCastDirectStreamSignalChunkPayload(signalId, fromSessionId, targetSessionId, signalType, i, count, part);
                await SendEnvelopeAsync(fromSessionId, targetSessionId, new RavaCastEnvelope(castId, RavaCastOp.DirectStreamSignalChunk, MessagePackSerializer.Serialize(chunk))).ConfigureAwait(false);
            }
        }
    }

    private static bool IsDirectStreamDescriptionSignal(string signalType)
    {
        var type = (signalType ?? string.Empty).Trim().ToLowerInvariant();
        return type is "offer" or "answer";
    }

    private void SendDirectStreamError(string targetSessionId, Guid castId, string message)
    {
        var mySession = GetMySessionId();
        if (string.IsNullOrWhiteSpace(mySession) || string.IsNullOrWhiteSpace(targetSessionId)) return;
        var payload = new RavaCastDirectStreamErrorPayload(mySession, message);
        SendEnvelope(mySession, targetSessionId, new RavaCastEnvelope(castId, RavaCastOp.DirectStreamError, MessagePackSerializer.Serialize(payload)));
    }
}
