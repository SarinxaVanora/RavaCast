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

    private void SendEnvelopeNearbyOnFramework(string fromSessionId, RavaCastEnvelope env)
    {
        var local = _objects.LocalPlayer;
        if (local is null) return;

        // Primary discovery path: everyone in the same live world/territory/instance registers the
        // same ephemeral area route. One advertisement reaches all RavaCast clients in that area.
        // This deliberately avoids depending on the host deriving every viewer's private route before
        // the viewer has ever spoken to us.
        var areaSessionId = _dalamudUtil.GetAreaSessionId();
        if (!string.IsNullOrWhiteSpace(areaSessionId))
            SendEnvelope(fromSessionId, areaSessionId, env);

        // Keep the old direct-target path as a compatibility/fallback lane. It is cheap at normal
        // venue sizes, makes mixed-version rollouts less brittle, and gives us a second path if an
        // individual client has not registered its area route yet. Duplicate adverts are harmless.
        foreach (var pc in _objects.OfType<IPlayerCharacter>().Where(p => p.Address != IntPtr.Zero && p.Address != local.Address).ToArray())
        {
            try
            {
                var sessionId = _dalamudUtil.GetSessionIdFromGameObject(pc);
                if (string.IsNullOrWhiteSpace(sessionId)) continue;
                SendEnvelope(fromSessionId, sessionId, env);
            }
            catch
            {
                // object table can shift between enumeration and ident lookup; harmless
            }
        }
    }

    private void RunOnFrameworkThreadSafe(Action action, string operation)
    {
        if (_framework.IsInFrameworkUpdateThread)
        {
            try { action(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to run RavaCast {operation}", operation); }
            return;
        }

        _ = RunOnFrameworkThreadSafeAsync(action, operation);
    }

    private async Task RunOnFrameworkThreadSafeAsync(Action action, string operation)
    {
        try
        {
            await _dalamudUtil.RunOnFrameworkThread(action).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to run RavaCast {operation} on framework thread", operation);
        }
    }

    private async Task SendEnvelopeSafeAsync(string fromSessionId, string targetSessionId, RavaCastEnvelope env)
    {
        try
        {
            await SendEnvelopeAsync(fromSessionId, targetSessionId, env).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send RavaCast mesh envelope");
        }
    }

    private Task SendEnvelopeAsync(string fromSessionId, string targetSessionId, RavaCastEnvelope env)
    {
        if (string.IsNullOrWhiteSpace(targetSessionId)) return Task.CompletedTask;
        var payload = BuildWirePayload(env);
        return _mesh.SendAsync(targetSessionId, new RavaGame(fromSessionId, payload));
    }

    private static byte[] BuildWirePayload(RavaCastEnvelope env)
    {
        var inner = MessagePackSerializer.Serialize(env);
        var buf = new byte[Magic.Length + inner.Length];
        Buffer.BlockCopy(Magic, 0, buf, 0, Magic.Length);
        Buffer.BlockCopy(inner, 0, buf, Magic.Length, inner.Length);
        return buf;
    }

    private static bool TryReadEnvelope(byte[] payload, out RavaCastEnvelope? env)
    {
        env = null;
        if (payload == null || payload.Length <= Magic.Length) return false;
        for (int i = 0; i < Magic.Length; i++)
            if (payload[i] != Magic[i]) return false;
        env = MessagePackSerializer.Deserialize<RavaCastEnvelope>(payload.AsSpan(Magic.Length).ToArray());
        return env is not null;
    }

    private string GetMySessionId()
    {
        try
        {
            if (!_clientState.IsLoggedIn || _objects.LocalPlayer is null) return string.Empty;
            return _dalamudUtil.GetLocalSessionId();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string HashPassword(string password, string salt)
    {
        if (string.IsNullOrEmpty(password)) return string.Empty;
        salt ??= string.Empty;
        var data = System.Text.Encoding.UTF8.GetBytes($"{salt}:{password}");
        var hash = System.Security.Cryptography.SHA256.HashData(data);
        return string.Concat(hash.Select(b => b.ToString("x2")));
    }

    public static bool TryValidatePublicWebUrl(string url, out Uri uri, out string error)
    {
        uri = null!;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(url))
        {
            error = "Enter a public web URL first.";
            return false;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed))
        {
            error = "That does not look like a valid absolute URL.";
            return false;
        }

        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            error = "RavaCast only supports public http/https web URLs. Local files are not supported.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(parsed.Host) || parsed.IsLoopback || string.Equals(parsed.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            error = "RavaCast only supports publicly available web sources. localhost/private sources are not supported.";
            return false;
        }

        if (IPAddress.TryParse(parsed.Host, out var ip) && IsPrivateAddress(ip))
        {
            error = "RavaCast only supports publicly available web sources. Private-network sources are not supported.";
            return false;
        }

        uri = parsed;
        return true;
    }

    private static bool IsPrivateAddress(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        var bytes = ip.GetAddressBytes();
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254)
                || bytes[0] == 0;
        }

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast
                || bytes[0] == 0xfc || bytes[0] == 0xfd;
        }

        return false;
    }
}
