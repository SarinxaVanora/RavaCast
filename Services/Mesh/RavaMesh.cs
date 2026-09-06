using Dalamud.Plugin.Services;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using RavaCast.Services.Mediator;

namespace RavaCast.Services.Mesh;

public sealed record RavaGame(string FromSessionId, byte[] Payload);
public interface IRavaMesh
{
    Task SendAsync(string sessionId, RavaGame message);
    bool IsConnected { get; }
    bool IsDiscoveryReady { get; }
    long SentCount { get; }
    long ReceivedCount { get; }
}

public sealed class RavaMesh : IRavaMesh, IAsyncDisposable
{
    private const string ServerBaseUrl = "https://RavaSync.ravalyn.uk";
    private const string Channel = "ravacast";
    private const int RequiredProtocolVersion = 2;
    private readonly ILogger<RavaMesh> _logger;
    private readonly PluginMediator _mediator;
    private readonly GameWorldService _world;
    private readonly IFramework _framework;
    private readonly HubConnection _hub;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private Task? _connectLoop;
    private string _localSessionId = string.Empty;
    private string _localAreaSessionId = string.Empty;
    private string _registeredSessionId = string.Empty;
    private string _registeredAreaSessionId = string.Empty;
    private long _lastSessionProbe;
    private long _sentCount;
    private long _receivedCount;
    private int _serverProtocolVersion;

    public bool IsConnected => _hub.State == HubConnectionState.Connected;
    public bool IsDiscoveryReady => IsConnected
        && !string.IsNullOrWhiteSpace(_registeredSessionId)
        && !string.IsNullOrWhiteSpace(_registeredAreaSessionId);
    public long SentCount => Interlocked.Read(ref _sentCount);
    public long ReceivedCount => Interlocked.Read(ref _receivedCount);

    public RavaMesh(ILogger<RavaMesh> logger, PluginMediator mediator, GameWorldService world, IFramework framework)
    {
        _logger = logger;
        _mediator = mediator;
        _world = world;
        _framework = framework;

        _hub = new HubConnectionBuilder()
            .WithUrl(ServerBaseUrl + "/ravamesh")
            .WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)])
            .Build();

        _hub.On<string, string, string, byte[]>("Client_MeshMessageV2", (channel, targetSessionId, fromSessionId, payload) =>
            OnMessageV2(channel, targetSessionId, fromSessionId, payload));
        _hub.Reconnecting += _ =>
        {
            ClearRegisteredRoutes();
            return Task.CompletedTask;
        };
        _hub.Reconnected += async _ =>
        {
            ClearRegisteredRoutes();
            await ProbeProtocolAsync().ConfigureAwait(false);
            await RegisterCurrentAsync().ConfigureAwait(false);
        };
        _hub.Closed += _ =>
        {
            ClearRegisteredRoutes();
            return Task.CompletedTask;
        };
    }

    public void Start()
    {
        _framework.Update += OnFrameworkUpdate;
        _connectLoop ??= Task.Run(ConnectionLoopAsync);
    }

    private void ClearRegisteredRoutes()
    {
        _registeredSessionId = string.Empty;
        _registeredAreaSessionId = string.Empty;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        var now = Environment.TickCount64;
        if (now - _lastSessionProbe < 1000) return;
        _lastSessionProbe = now;

        var session = _world.GetLocalSessionId();
        var areaSession = _world.GetAreaSessionId();
        if (string.Equals(session, _localSessionId, StringComparison.Ordinal)
            && string.Equals(areaSession, _localAreaSessionId, StringComparison.Ordinal))
            return;

        var oldSession = _localSessionId;
        var oldAreaSession = _localAreaSessionId;
        _localSessionId = session;
        _localAreaSessionId = areaSession;
        ClearRegisteredRoutes();

        _ = Task.Run(async () =>
        {
            try
            {
                if (IsConnected && !string.IsNullOrWhiteSpace(oldSession) && !string.Equals(oldSession, session, StringComparison.Ordinal))
                    await _hub.InvokeAsync("MeshUnregister", Channel, oldSession, _cts.Token).ConfigureAwait(false);

                if (IsConnected && !string.IsNullOrWhiteSpace(oldAreaSession) && !string.Equals(oldAreaSession, areaSession, StringComparison.Ordinal))
                    await _hub.InvokeAsync("MeshUnregister", Channel, oldAreaSession, _cts.Token).ConfigureAwait(false);

                await RegisterCurrentAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RavaMesh route refresh failed; it will be retried.");
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
                            ClearRegisteredRoutes();

                            await ProbeProtocolAsync().ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        _connectGate.Release();
                    }
                }

                if (IsConnected)
                    await RegisterCurrentAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RavaMesh connection/registration failed; retrying.");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }


    private async Task ProbeProtocolAsync()
    {
        try
        {
            _serverProtocolVersion = await _hub.InvokeAsync<int>("MeshProtocolVersion", _cts.Token).ConfigureAwait(false);
            if (_serverProtocolVersion < RequiredProtocolVersion)
                _logger.LogWarning("RavaMesh server protocol {version} is too old; RavaCast requires protocol {required} for lobby relay.", _serverProtocolVersion, RequiredProtocolVersion);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _serverProtocolVersion = 0;
            _logger.LogWarning(ex, "RavaMesh protocol probe failed; lobby relay will wait for a successful probe.");
        }
    }

    private async Task RegisterCurrentAsync()
    {
        if (!IsConnected) return;

        var session = _localSessionId;
        var areaSession = _localAreaSessionId;

        if (!string.IsNullOrWhiteSpace(session) && !string.Equals(session, _registeredSessionId, StringComparison.Ordinal))
        {
            await _hub.InvokeAsync("MeshRegister", Channel, session, _cts.Token).ConfigureAwait(false);
            _registeredSessionId = session;
            _logger.LogInformation("RavaMesh registered standalone RavaCast character route {sessionId}.", session);
        }

        if (!string.IsNullOrWhiteSpace(areaSession) && !string.Equals(areaSession, _registeredAreaSessionId, StringComparison.Ordinal))
        {
            await _hub.InvokeAsync("MeshRegister", Channel, areaSession, _cts.Token).ConfigureAwait(false);
            _registeredAreaSessionId = areaSession;
            _logger.LogInformation("RavaMesh registered standalone RavaCast discovery route {areaSessionId}.", areaSession);
        }
    }

    public async Task SendAsync(string sessionId, RavaGame message)
    {
        if (!IsConnected || string.IsNullOrWhiteSpace(sessionId)) return;

        var from = string.IsNullOrWhiteSpace(message.FromSessionId) ? _localSessionId : message.FromSessionId;
        if (string.IsNullOrWhiteSpace(from)) return;

        try
        {
            if (string.IsNullOrWhiteSpace(_localSessionId))
                _localSessionId = from;

            if (string.IsNullOrWhiteSpace(_localAreaSessionId))
                _localAreaSessionId = _world.GetAreaSessionId();

            if (string.Equals(from, _localSessionId, StringComparison.Ordinal))
                await RegisterCurrentAsync().ConfigureAwait(false);
            else
                await _hub.InvokeAsync("MeshRegister", Channel, from, _cts.Token).ConfigureAwait(false);

            if (_serverProtocolVersion < RequiredProtocolVersion)
                await ProbeProtocolAsync().ConfigureAwait(false);

            if (_serverProtocolVersion < RequiredProtocolVersion)
            {
                _logger.LogWarning("RavaMesh send skipped because server protocol {version} is below required protocol {required}.", _serverProtocolVersion, RequiredProtocolVersion);
                return;
            }

            await _hub.InvokeAsync("MeshSendV2", Channel, sessionId, from, message.Payload ?? [], _cts.Token).ConfigureAwait(false);
            Interlocked.Increment(ref _sentCount);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ClearRegisteredRoutes();
            _logger.LogWarning(ex, "RavaMesh send to {sessionId} failed; registration will be retried.", sessionId);
        }
    }

    private void OnMessageV2(string channel, string targetSessionId, string fromSessionId, byte[] payload)
    {
        if (!string.Equals(channel, Channel, StringComparison.OrdinalIgnoreCase)) return;
        Interlocked.Increment(ref _receivedCount);
        _mediator.Publish(new MeshPayloadMessage(targetSessionId ?? string.Empty, fromSessionId ?? string.Empty, payload ?? []));
    }

    public async ValueTask DisposeAsync()
    {
        _framework.Update -= OnFrameworkUpdate;
        _cts.Cancel();

        try
        {
            if (IsConnected && !string.IsNullOrWhiteSpace(_localSessionId))
                await _hub.InvokeAsync("MeshUnregister", Channel, _localSessionId).ConfigureAwait(false);
            if (IsConnected && !string.IsNullOrWhiteSpace(_localAreaSessionId))
                await _hub.InvokeAsync("MeshUnregister", Channel, _localAreaSessionId).ConfigureAwait(false);
        }
        catch { }

        try { await _hub.StopAsync().ConfigureAwait(false); } catch { }
        try { await _hub.DisposeAsync().ConfigureAwait(false); } catch { }
        _connectGate.Dispose();
        _cts.Dispose();
    }
}
