using System;
using System.IO;
using System.Reflection;
using Wino.Mail.AI.Cryptography;

namespace Wino.Services;

internal static class EmbeddedIntelligencePublicKeyProvider
{
    public const string KeyId = "wino-intelligence-2026-08-v1";
    private const string ResourceName = "Wino.Services.Security.Keys.intelligence-public.pem";

    public static ContentEncryptionPublicKey Load()
    {
        using var stream = typeof(EmbeddedIntelligencePublicKeyProvider).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded intelligence public key '{ResourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return new ContentEncryptionPublicKey(KeyId, reader.ReadToEnd());
    }
}
