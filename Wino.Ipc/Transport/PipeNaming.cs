using System;
using System.Security.Cryptography;
using System.Text;

namespace Wino.Ipc.Transport;

public static class PipeNaming
{
    /// <summary>
    /// Builds the deterministic pipe name shared by the UI and the background service:
    /// <c>wino-ipc-{packageFamilyHash}-{sessionId}</c>. The package family name is hashed
    /// to stay within pipe name length limits, and the session id isolates fast-user-switching sessions.
    /// </summary>
    public static string GetPipeName(string packageFamilyName, int sessionId)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(packageFamilyName ?? string.Empty));
        var shortHash = Convert.ToHexString(hashBytes.AsSpan(0, 8)).ToLowerInvariant();

        return $"wino-ipc-{shortHash}-{sessionId}";
    }
}
