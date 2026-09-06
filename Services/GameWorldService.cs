using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using RavaCast.Services.Mesh;
using System.Security.Cryptography;
using System.Text;

namespace RavaCast.Services;

public sealed class GameWorldService
{
    private readonly IObjectTable _objects;
    private readonly IFramework _framework;
    private readonly IClientState _clientState;

    public GameWorldService(IObjectTable objects, IFramework framework, IClientState clientState)
    {
        _objects = objects;
        _framework = framework;
        _clientState = clientState;
    }

    /// <summary>
    /// Builds a stable privacy-preserving identity for a player from values every nearby client can
    /// independently resolve: character name + home world.
    /// </summary>
    public string? GetIdentFromGameObject(IGameObject? gameObject)
    {
        if (gameObject is not IPlayerCharacter player || player.Address == nint.Zero) return null;

        try
        {
            var name = player.Name.TextValue?.Trim();
            var homeWorldId = player.HomeWorld.RowId;
            if (string.IsNullOrWhiteSpace(name) || homeWorldId == 0) return null;

            var canonicalIdentity = $"{name.Normalize(NormalizationForm.FormKC).ToUpperInvariant()}@{homeWorldId}";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalIdentity)));
        }
        catch
        {
            return null;
        }
    }

    public string GetSessionIdFromGameObject(IGameObject? gameObject)
    {
        var ident = GetIdentFromGameObject(gameObject);
        return string.IsNullOrWhiteSpace(ident) ? string.Empty : RavaSessionId.FromIdent(ident);
    }

    public string GetLocalSessionId() => GetSessionIdFromGameObject(_objects.LocalPlayer);

    /// <summary>
    /// Shared discovery route for everyone in the same live game instance. Multiple RavaCast clients
    /// intentionally register the same route. This removes lobby discovery's dependency on deriving
    /// another player's private per-character route correctly before the first advertisement arrives.
    /// </summary>
    public string GetAreaSessionId()
    {
        try
        {
            if (!_clientState.IsLoggedIn || _objects.LocalPlayer is not IPlayerCharacter local) return string.Empty;

            var currentWorldId = local.CurrentWorld.RowId;
            var territoryId = _clientState.TerritoryType;
            var instance = _clientState.Instance;
            if (currentWorldId == 0 || territoryId == 0) return string.Empty;

            var canonicalArea = $"RAVACAST-AREA-V1|{currentWorldId}|{territoryId}|{instance}";
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalArea));
            return "AREA-" + Convert.ToHexString(hash.AsSpan(0, 16));
        }
        catch
        {
            return string.Empty;
        }
    }

    public int GetVisiblePlayerCount()
    {
        try
        {
            var localAddress = _objects.LocalPlayer?.Address ?? nint.Zero;
            return _objects.OfType<IPlayerCharacter>().Count(p => p.Address != nint.Zero && p.Address != localAddress);
        }
        catch
        {
            return 0;
        }
    }

    public bool IsPlayerNameVisible(string? playerName)
    {
        if (string.IsNullOrWhiteSpace(playerName)) return false;

        try
        {
            var localAddress = _objects.LocalPlayer?.Address ?? nint.Zero;
            foreach (var player in _objects.OfType<IPlayerCharacter>())
            {
                if (player.Address == nint.Zero || player.Address == localAddress) continue;
                if (string.Equals(player.Name.TextValue?.Trim(), playerName.Trim(), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
            // Object table can shift during zone/object teardown.
        }

        return false;
    }

    public async Task RunOnFrameworkThread(Action action)
    {
        if (_framework.IsInFrameworkUpdateThread)
        {
            action();
            return;
        }

        await _framework.RunOnFrameworkThread(action).ConfigureAwait(false);
    }
}
