using System.Security.Cryptography;
using System.Text;

namespace RavaCast.Services.Mesh;

public static class RavaSessionId
{
    private const string SessionSalt = "RavaMesh-2026-SessionSalt";
    public static string FromIdent(string ident)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{SessionSalt}|{ident}"));
        return Convert.ToHexString(hash.AsSpan(0, 16));
    }
}
