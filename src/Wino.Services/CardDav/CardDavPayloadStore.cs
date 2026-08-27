using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services.CardDav;

public sealed class CardDavPayloadStore : ICardDavPayloadStore
{
    private readonly string _payloadFolder;

    public CardDavPayloadStore(IApplicationConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _payloadFolder = Path.Combine(configuration.ApplicationDataFolderPath, "contacts", "carddav");
        Directory.CreateDirectory(_payloadFolder);
    }

    public async Task<string> SaveAsync(string content, CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes(content ?? string.Empty);
        var reference = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() + ".vcf";
        var destination = Resolve(reference);
        if (File.Exists(destination))
            return reference;

        Directory.CreateDirectory(_payloadFolder);
        var temporary = Path.Combine(_payloadFolder, $".{Guid.NewGuid():N}.tmp");
        await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
        try
        {
            File.Move(temporary, destination, overwrite: false);
        }
        catch (IOException) when (File.Exists(destination))
        {
            File.Delete(temporary);
        }
        return reference;
    }

    public Task<string> ReadAsync(string reference, CancellationToken cancellationToken = default)
        => File.ReadAllTextAsync(Resolve(reference), Encoding.UTF8, cancellationToken);

    public Task DeleteUnreferencedAsync(IReadOnlySet<string> referencedPayloads, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_payloadFolder)) return Task.CompletedTask;
        foreach (var path in Directory.EnumerateFiles(_payloadFolder, "*.vcf"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (referencedPayloads?.Contains(Path.GetFileName(path)) != true)
                File.Delete(path);
        }
        foreach (var path in Directory.EnumerateFiles(_payloadFolder, ".*.tmp")
                     .Where(path => File.GetCreationTimeUtc(path) < DateTime.UtcNow.AddHours(-1)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(path);
        }
        return Task.CompletedTask;
    }

    private string Resolve(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !string.Equals(reference, Path.GetFileName(reference), StringComparison.Ordinal))
        {
            throw new ArgumentException("Invalid CardDAV payload reference.", nameof(reference));
        }
        return Path.Combine(_payloadFolder, reference);
    }
}
