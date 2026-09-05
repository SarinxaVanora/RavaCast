using Dalamud.Plugin.Services;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using RavaCast.Services.Mediator;

namespace RavaCast.Services.Mesh;

public sealed record RavaGame(string FromSessionId, byte[] Payload);
public interface IRavaMesh { Task SendAsync(string sessionId, RavaGame message); bool IsConnected { get; } }

public sealed class RavaMesh : IRavaMesh, IAsyncDisposable
{
    private const string ServerBaseUrl = "https://RavaSync.ravalyn.uk";
    private const string Channel = "ravacast";
    private readonly ILogger<RavaMesh> _logger;
    private readonly PluginMediator _mediator;
    private readonly GameWorldService _world;
    private readonly IFramework _framework;
    private readonly HubConnection _hub;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private Task? _connectLoop;
    private string _localSessionId = string.Empty;
    private long _lastSessionProbe;

    public bool IsConnected => _hub.State == HubConnectionState.Connected;

    public RavaMesh(ILogger<RavaMesh> logger, PluginMediator mediator, GameWorldService world, IFramework framework)
    {
        _logger = logger; _mediator = mediator; _world = world; _framework = framework;
        _hub = new HubConnectionBuilder()
            .WithUrl(ServerBaseUrl + "/ravamesh")
            .WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)])
            .Build();
        _hub.On<RavaMeshMessage>("Client_MeshMessage", OnMessage);
        _hub.Reconnected += async _ => await RegisterCurrentAsync().ConfigureAwait(false);
    }

    public void Start()
    {
        _framework.Update += OnFrameworkUpdate;
        _connectLoop ??= Task.Run(ConnectionLoopAsync);
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        var now = Environment.TickCount64;
        if (now - _lastSessionProbe < 1000) return;
        _lastSessionProbe = now;
        var session = _world.GetLocalSessionId();
        if (string.Equals(session, _localSessionId, StringComparison.Ordinal)) return;
        var old = _localSessionId;
        _localSessionId = session;
        _ = Task.Run(async () =>
        {
            try
            {
                if (IsConnected && !string.IsNullOrWhiteSpace(old)) await _hub.InvokeAsync("MeshUnregister", Channel, old).ConfigureAwait(false);
                await RegisterCurrentAsync().ConfigureAwait(false);
            }
            catch { }
        });
    }

    private async Task ConnectionLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                if (_hub.State == HubConnectionState.Disconnected)
                {
                    await _connectGate.WaitAsync(_cts.Token).ConfigureAwait(false);
                    try
                    {
                        if (_hub.State == HubConnectionState.Disconnected)
                        {
                            await _hub.StartAsync(_cts.Token).ConfigureAwait(false);
                            var version = await _hub.InvokeAsync<int>("MeshProtocolVersion", _cts.Token).ConfigureAwait(false);
                            if (version != 1) _logger.LogWarning("RavaMesh protocol version {version} differs from expected version 1.", version);
                            await RegisterCurrentAsync().ConfigureAwait(false);
                        }
                    }
                    finally { _connectGate.Release(); }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "RavaMesh connection failed; retrying."); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task RegisterCurrentAsync()
    {
        var session = _localSessionId;
        if (!IsConnected || string.IsNullOrWhiteSpace(session)) return;
        await _hub.InvokeAsync("MeshRegister", Channel, session, _cts.Token).ConfigureAwait(false);
    }

    public async Task SendAsync(string sessionId, RavaGame message)
    {
        if (!IsConnected || string.IsNullOrWhiteSpace(sessionId)) return;
        var from = string.IsNullOrWhiteSpace(message.FromSessionId) ? _localSessionId : message.FromSessionId;
        if (string.IsNullOrWhiteSpace(from)) return;
        try
        {
            await _hub.InvokeAsync("MeshSend", new RavaMeshMessage { Channel = Channel, FromSessionId = from, TargetSessionId = sessionId, Payload = message.Payload ?? [] }, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "RavaMesh send to {sessionId} failed.", sessionId); }
    }

    private void OnMessage(RavaMeshMessage msg)
    {
        if (!string.Equals(msg.Channel, Channel, StringComparison.OrdinalIgnoreCase)) return;
        _mediator.Publish(new MeshPayloadMessage(msg.TargetSessionId ?? string.Empty, msg.FromSessionId ?? string.Empty, msg.Payload ?? []));
    }

    public async ValueTask DisposeAsync()
    {
        _framework.Update -= OnFrameworkUpdate;
        _cts.Cancel();
        try { if (!string.IsNullOrWhiteSpace(_localSessionId) && IsConnected) await _hub.InvokeAsync("MeshUnregister", Channel, _localSessionId).ConfigureAwait(false); } catch { }
        try { await _hub.StopAsync().ConfigureAwait(false); } catch { }
        try { await _hub.DisposeAsync().ConfigureAwait(false); } catch { }
        _connectGate.Dispose(); _cts.Dispose();
    }
}

public sealed class RavaMeshMessage
{
    public string Channel { get; set; } = string.Empty;
    public string TargetSessionId { get; set; } = string.Empty;
    public string FromSessionId { get; set; } = string.Empty;
    public byte[] Payload { get; set; } = [];
}
