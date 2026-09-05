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

public sealed partial class RavaCastService : DisposableMediatorSubscriberBase
{
    private readonly ILogger<RavaCastService> _logger;
    private readonly IRavaMesh _mesh;
    private readonly GameWorldService _dalamudUtil;
    private readonly IObjectTable _objects;
    private readonly IClientState _clientState;
    private readonly IFramework _framework;
    private readonly RavaCastConfigService _config;
    private readonly RavaCastBrowserSurface _surface;
    private readonly PerformanceMonitor _performanceCollector;

    private static readonly byte[] Magic = [(byte)'R', (byte)'A', (byte)'V', (byte)'A', (byte)'C', (byte)'A', (byte)'S', (byte)'T', 0];
    private const int BroadcastIntervalMs = 2500;
    private const int CastTtlMs = 9000;
    private const int SurfaceSyncIntervalMs = 1000;
    private const int DirectStreamSignalChunkChars = 900;
    private const int DirectStreamSignalAssemblyTtlMs = 30000;
    private const int LivePlaneBroadcastIntervalMs = 250;
    private const int LivePlaneFinalDebounceMs = 300;
    private const int PlaybackRecoveryIntervalMs = 1250;
    private const int HostedObservedUrlSyncDebounceMs = 1200;
    private const int HostedObservedUrlSyncMinimumIntervalMs = 1800;
    private static readonly int[] PasswordProtectedJoinRetryDelaysMs = [350, 900, 1800, 3200, 5200];

    private long _lastBroadcastTick;
    private long _lastPruneTick;
    private long _lastSurfaceSyncTick;
    private long _lastHostedPlaneBroadcastTick;
    private long _pendingHostedPlaneFinalBroadcastTick;
    private long _pendingHostedBrowserNavigationSyncTick;
    private long _lastPlaybackRecoveryTick;
    private long _pendingHostedObservedUrlSinceTick;
    private long _lastHostedObservedUrlBroadcastTick;
    private string _pendingHostedObservedUrl = string.Empty;
    private readonly ConcurrentDictionary<Guid, RavaCastSummary> _activeCasts = new();
    private readonly ConcurrentDictionary<string, bool> _joinedViewers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _directStreamReadyViewers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, bool> _pendingJoinMuted = new();
    private readonly ConcurrentDictionary<Guid, long> _pendingPasswordProtectedJoinRetries = new();
    private readonly ConcurrentDictionary<string, DirectStreamSignalAssembly> _directStreamSignalAssemblies = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _completedDirectStreamSignalAssemblies = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _directStreamSignalSendGates = new(StringComparer.Ordinal);

    private HostedCast? _hosted;
    private JoinedCast? _joined;
    private float _localVolume = 0.50f;
    private volatile bool _worldBrowserInputSuspended;

    public bool WorldBrowserInputSuspended => _worldBrowserInputSuspended;

    public void SetWorldBrowserInputSuspended(bool suspended)
    {
        _worldBrowserInputSuspended = suspended;
        if (suspended)
            _surface.SendBrowserFocus(false);
    }

    private sealed class HostedCast
    {
        public Guid CastId { get; init; } = Guid.NewGuid();
        public string HostSessionId { get; init; } = string.Empty;
        public string HostName { get; init; } = "Player";
        public string CastName { get; set; } = "RavaCast";
        public string Url { get; set; } = string.Empty;
        public string SourceDomain { get; set; } = string.Empty;
        public string MediaTitle { get; set; } = string.Empty;
        public bool IsPlaying { get; set; } = true;
        public double PositionSeconds { get; set; }
        public double? DurationSeconds { get; set; }
        public DateTime StateUtc { get; set; } = DateTime.UtcNow;
        public RavaCastPlane Plane { get; set; } = EmptyPlane;
        public List<string> Queue { get; } = [];
        public RavaCastCookiePayload[] ConsentCookies { get; set; } = [];
        public string PasswordSalt { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
        public bool PasswordProtected => !string.IsNullOrEmpty(PasswordHash);
        public RavaCastMode Mode { get; set; } = RavaCastMode.UrlShare;
        public RavaCastDirectStreamQuality DirectStreamQuality { get; set; } = RavaCastDirectStreamQuality.Normal720p30;
        public bool DirectStreamPublisherRequested { get; set; }
        public string DirectStreamStatus { get; set; } = string.Empty;
        public string DirectStreamDetail { get; set; } = string.Empty;
        public long NavigationRevision { get; set; }
    }

    private sealed class JoinedCast
    {
        public Guid CastId { get; set; }
        public string HostSessionId { get; set; } = string.Empty;
        public string ViewerSessionId { get; set; } = string.Empty;
        public string HostName { get; set; } = string.Empty;
        public string CastName { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string SourceDomain { get; set; } = string.Empty;
        public string MediaTitle { get; set; } = string.Empty;
        public bool IsPlaying { get; set; } = true;
        public double PositionSeconds { get; set; }
        public double? DurationSeconds { get; set; }
        public DateTime StateUtc { get; set; } = DateTime.UtcNow;
        public int JoinedCount { get; set; }
        public RavaCastPlane Plane { get; set; } = EmptyPlane;
        public IReadOnlyList<string> Queue { get; set; } = [];
        public IReadOnlyList<RavaCastCookiePayload> ConsentCookies { get; set; } = [];
        public bool PasswordProtected { get; set; }
        public string PasswordSalt { get; set; } = string.Empty;
        public bool IsMuted { get; set; }
        public float Volume { get; set; } = 0.5f;
        public RavaCastMode Mode { get; set; } = RavaCastMode.UrlShare;
        public RavaCastDirectStreamQuality DirectStreamQuality { get; set; } = RavaCastDirectStreamQuality.Normal720p30;
        public string DirectStreamStatus { get; set; } = string.Empty;
        public string DirectStreamDetail { get; set; } = string.Empty;
        public bool DirectStreamReceiverRequested { get; set; }
        public long NavigationRevision { get; set; }
    }

    private sealed class DirectStreamSignalAssembly
    {
        public DirectStreamSignalAssembly(Guid castId, string signalId, string fromSessionId, string toSessionId, string type, int chunkCount)
        {
            CastId = castId;
            SignalId = signalId;
            FromSessionId = fromSessionId;
            ToSessionId = toSessionId;
            Type = type;
            ChunkCount = Math.Clamp(chunkCount, 1, 256);
            Chunks = new string[ChunkCount];
            Received = new bool[ChunkCount];
            CreatedTick = Environment.TickCount64;
        }

        public Guid CastId { get; }
        public string SignalId { get; }
        public string FromSessionId { get; }
        public string ToSessionId { get; }
        public string Type { get; }
        public int ChunkCount { get; }
        public string[] Chunks { get; }
        public bool[] Received { get; }
        public int ReceivedCount { get; private set; }
        public long CreatedTick { get; }

        public bool TryAdd(int index, string part)
        {
            if (index < 0 || index >= ChunkCount) return false;
            if (!Received[index])
            {
                Received[index] = true;
                Chunks[index] = part ?? string.Empty;
                ReceivedCount++;
            }
            return ReceivedCount >= ChunkCount;
        }

        public string BuildPayloadJson() => string.Concat(Chunks);
    }


    public static readonly RavaCastPlane EmptyPlane = new(string.Empty, Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero);

    public RavaCastService(ILogger<RavaCastService> logger, PluginMediator mediator, IRavaMesh mesh, GameWorldService dalamudUtil,
        IObjectTable objects, IClientState clientState, IFramework framework, RavaCastConfigService config, RavaCastBrowserSurface surface, PerformanceMonitor performanceCollector)
        : base(logger, mediator)
    {
        _logger = logger;
        _mesh = mesh;
        _dalamudUtil = dalamudUtil;
        _objects = objects;
        _clientState = clientState;
        _framework = framework;
        _config = config;
        _surface = surface;
        _performanceCollector = performanceCollector;

        _localVolume = GetDefaultVolume();

        Mediator.Subscribe<MeshPayloadMessage>(this, OnGameMesh);
    }

    public bool TryStartBroadcast(string castName, string url, RavaCastPlane plane, out string error)
        => TryStartBroadcast(castName, url, plane, RavaCastMode.UrlShare, RavaCastDirectStreamQuality.Normal720p30, null, out error);

    public bool TryStartBroadcast(string castName, string url, RavaCastPlane plane, RavaCastMode mode, RavaCastDirectStreamQuality quality, out string error)
        => TryStartBroadcast(castName, url, plane, mode, quality, null, out error);

    private void CancelPendingHostedPlaneBroadcast()
        => _pendingHostedPlaneFinalBroadcastTick = 0;

    private void CancelPendingHostedBrowserNavigationSync()
        => _pendingHostedBrowserNavigationSyncTick = 0;

    public void Join(Guid castId, bool muted) => Join(castId, muted, null);

    public void ReloadCurrentBrowser()
        => _surface.ReloadPage();

    private static Vector3 ScreenRightFromYaw(float yaw) => new(-MathF.Cos(yaw), 0f, MathF.Sin(yaw));
    private static Vector3 ScreenNormalFromYaw(float yaw) => new(MathF.Sin(yaw), 0f, MathF.Cos(yaw));
    private static float DegreesToRadians(float degrees) => degrees * (MathF.PI / 180f);

    private static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static string BuildDirectStreamSignalAssemblyKey(Guid castId, string signalId, string fromSessionId, string toSessionId, string type)
        => $"{castId:D}|{signalId}|{fromSessionId}|{toSessionId}|{type}";

    private void SendDirectStreamSignal(string fromSessionId, string targetSessionId, Guid castId, string signalType, string payloadJson)
        => _ = SendDirectStreamSignalAsync(fromSessionId, targetSessionId, castId, signalType, payloadJson);

    private static bool ShouldChunkDirectStreamSignal(string signalType, string payloadJson)
        => IsDirectStreamDescriptionSignal(signalType) || (payloadJson?.Length ?? 0) > DirectStreamSignalChunkChars;

    private static RavaCastOp DirectStreamSignalOp(string signalType)
        => (signalType ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "offer" => RavaCastOp.DirectStreamOffer,
            "answer" => RavaCastOp.DirectStreamAnswer,
            "ice" or "candidate" or "icecandidate" => RavaCastOp.DirectStreamIce,
            _ => RavaCastOp.DirectStreamIce
        };

    private void SendEnvelopeNearby(string fromSessionId, RavaCastEnvelope env)
        => RunOnFrameworkThreadSafe(() => SendEnvelopeNearbyOnFramework(fromSessionId, env), "nearby envelope send");

    private void SendEnvelope(string fromSessionId, string targetSessionId, RavaCastEnvelope env)
        => _ = SendEnvelopeSafeAsync(fromSessionId, targetSessionId, env);
}
