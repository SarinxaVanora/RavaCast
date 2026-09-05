using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
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

    public unsafe string? GetIdentFromGameObject(IGameObject? gameObject)
    {
        if (gameObject is not IPlayerCharacter player || player.Address == nint.Zero) return null;
        try
        {
            var cid = ((BattleChara*)player.Address)->Character.ContentId;
            if (cid == 0) return null;
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cid.ToString())));
        }
        catch
        {
            return null;
        }
    }

    public string GetLocalSessionId()
    {
        var ident = GetIdentFromGameObject(_objects.LocalPlayer);
        return string.IsNullOrWhiteSpace(ident) ? string.Empty : RavaSessionId.FromIdent(ident);
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
