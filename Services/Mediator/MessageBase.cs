namespace RavaCast.Services.Mediator;

public abstract record MessageBase
{
    public virtual bool KeepThreadContext => false;
}

public sealed record UiToggleMessage(Type UiType) : MessageBase;
public sealed record MeshPayloadMessage(string LocalSessionId, string FromSessionId, byte[] Payload) : MessageBase;
