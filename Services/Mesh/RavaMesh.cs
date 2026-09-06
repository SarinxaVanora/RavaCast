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
    private string _registeredSessionId = string.Empty;
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
        _hub.Reconnecting += _ =>
        {
            _registeredSessionId = string.Empty;
            return Task.CompletedTask;
        };
        _hub.Reconnected += async _ =>
        {
            _registeredSessionId = string.Empty;
            await RegisterCurrentAsync().ConfigureAwait(false);
        };
        _hub.Closed += _ =>
        {
            _registeredSessionId = string.Empty;
            return Task.CompletedTask;
        };
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
        _registeredSessionId = string.Empty;

        _ = Task.Run(async () =>
        {
            try
            {
                if (IsConnected && !string.IsNullOrWhiteSpace(old))
                    await _hub.InvokeAsync("MeshUnregister", Channel, old, _cts.Token).ConfigureAwait(false);

                await RegisterCurrentAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RavaMesh session route refresh failed; it will be retried.");
            }
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
                            _registeredSessionId = string.Empty;

                            // Protocol probing is diagnostic only. A probe failure must never leave a
                            // connected SignalR socket permanently unregistered.
                            try
                            {
                                var version = await _hub.InvokeAsync<int>("MeshProtocolVersion", _cts.Token).ConfigureAwait(false);
                                if (version != 1)
                                    _logger.LogWarning("RavaMesh protocol version {version} differs from expected version 1.", version);
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                _logger.LogWarning(ex, "RavaMesh protocol probe failed; continuing with route registration.");
                            }
                        }
                    }
                    finally
                    {
                        _connectGate.Release();
                    }
                }

                // This is deliberately attempted even when the socket was already connected. It heals
                // the connected-but-unregistered state after any transient registration failure.
                if (IsConnected)
                    await RegisterCurrentAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "RavaMesh connection/registration failed; retrying."); }

            try { await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RegisterCurrentAsync()
    {
        var session = _localSessionId;
        if (!IsConnected || string.IsNullOrWhiteSpace(session)) return;
        if (string.Equals(session, _registeredSessionId, StringComparison.Ordinal)) return;

        await _hub.InvokeAsync("MeshRegister", Channel, session, _cts.Token).ConfigureAwait(false);
        _registeredSessionId = session;
        _logger.LogInformation("RavaMesh registered standalone RavaCast route {sessionId}.", session);
    }

    public async Task SendAsync(string sessionId, RavaGame message)
    {
        if (!IsConnected || string.IsNullOrWhiteSpace(sessionId)) return;

        var from = string.IsNullOrWhiteSpace(message.FromSessionId) ? _localSessionId : message.FromSessionId;
        if (string.IsNullOrWhiteSpace(from)) return;

        try
        {
            // MeshSend is rejected server-side unless the source route is registered. Ensure the
            // source route is healthy immediately before sending rather than silently dropping a cast.
            // A cast can be started before the one-second framework identity probe has populated
            // _localSessionId, so adopt the already-derived local sender route on that first send.
            if (string.IsNullOrWhiteSpace(_localSessionId))
                _localSessionId = from;

            if (string.Equals(from, _localSessionId, StringComparison.Ordinal))
                await RegisterCurrentAsync().ConfigureAwait(false);
            else
                await _hub.InvokeAsync("MeshRegister", Channel, from, _cts.Token).ConfigureAwait(false);

            await _hub.InvokeAsync("MeshSend", new RavaMeshMessage
            {
                Channel = Channel,
                FromSessionId = from,
                TargetSessionId = sessionId,
                Payload = message.Payload ?? []
            }, _cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _registeredSessionId = string.Empty;
            _logger.LogWarning(ex, "RavaMesh send to {sessionId} failed; registration will be retried.", sessionId);
        }
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
