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

    public GameWorldService(IObjectTable objects, IFramework framework)
    {
        _objects = objects;
        _framework = framework;
    }

    /// <summary>
    /// Builds a stable, privacy-preserving identity for any visible player using information every
    /// nearby client can resolve independently: character name + home-world row id.
    ///
    /// Do not use remote ContentId here. FFXIV does not reliably expose another player's ContentId
    /// through the object table, which caused standalone RavaCast lobby advertisements to have no
    /// destination route for most nearby players.
    /// </summary>
    public string? GetIdentFromGameObject(IGameObject? gameObject)
    {
        if (gameObject is not IPlayerCharacter player || player.Address == nint.Zero) return null;

        try
        {
            var name = player.Name.TextValue?.Trim();
            var homeWorldId = player.HomeWorld.RowId;
            if (string.IsNullOrWhiteSpace(name) || homeWorldId == 0) return null;

            // Normalise the visible identity before hashing so every client derives the same route.
            // Only the hash is handed to RavaSessionId/RavaMesh; character names are never sent as routes.
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
