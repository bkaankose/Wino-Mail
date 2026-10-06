using System.Runtime.Versioning;
using System.Security.Cryptography;
using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.Windows;

/// <summary>Preserves Windows CurrentUser DPAPI with caller-supplied legacy entropy.</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    public byte[] Protect(byte[] data, byte[] context)
        => ProtectedData.Protect(data, context, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] data, byte[] context)
        => ProtectedData.Unprotect(data, context, DataProtectionScope.CurrentUser);
}
