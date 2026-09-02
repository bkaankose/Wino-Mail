using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Connectivity;

namespace Wino.Services;

public class KnownImapProviderCatalog : IKnownImapProviderCatalog
{
    private readonly int _schemaVersion;

    public KnownImapProviderCatalog(KnownImapProviderCatalogDocument document)
    {
        KnownImapProviderCatalogLoader.Validate(document);
        _schemaVersion = document.SchemaVersion;

        Providers = Array.AsReadOnly(document.Providers.Select(provider => provider with
        {
            EmailDomains = Array.AsReadOnly(provider.EmailDomains.ToArray()),
            IncomingHosts = Array.AsReadOnly(provider.IncomingHosts.ToArray()),
            FolderAliases = Array.AsReadOnly(provider.FolderAliases.ToArray())
        }).ToArray());
        SetupProviders = Array.AsReadOnly(Providers
            .Where(provider => provider.SetupVisible)
            .OrderBy(provider => provider.SetupOrder)
            .ToArray());
        GenericFolderAliases = Array.AsReadOnly(document.GenericFolderAliases.ToArray());
    }

    public int SchemaVersion => _schemaVersion;
    public IReadOnlyList<KnownImapProviderDefinition> Providers { get; }
    public IReadOnlyList<KnownImapProviderDefinition> SetupProviders { get; }
    public IReadOnlyList<KnownImapFolderAlias> GenericFolderAliases { get; }

    public KnownImapProviderDefinition GetBySpecialProvider(SpecialImapProvider provider)
        => Providers.FirstOrDefault(candidate => candidate.SpecialImapProvider == provider);

    public KnownImapProviderDefinition Match(
        string emailAddress,
        string incomingHost,
        SpecialImapProvider preferredProvider = SpecialImapProvider.None)
    {
        if (preferredProvider != SpecialImapProvider.None)
        {
            var preferred = GetBySpecialProvider(preferredProvider);
            if (preferred != null)
                return preferred;
        }

        if (!string.IsNullOrWhiteSpace(incomingHost))
        {
            var byHost = Providers.FirstOrDefault(provider => provider.IncomingHosts.Any(
                host => string.Equals(host, incomingHost.Trim(), StringComparison.OrdinalIgnoreCase)));
            if (byHost != null)
                return byHost;
        }

        var domain = GetDomain(emailAddress);
        return string.IsNullOrEmpty(domain)
            ? null
            : Providers.FirstOrDefault(provider => provider.EmailDomains.Any(
                candidate => string.Equals(candidate, domain, StringComparison.OrdinalIgnoreCase)));
    }

    public string ResolveUsername(ImapUsernamePolicy policy, string emailAddress)
    {
        var normalized = emailAddress?.Trim() ?? string.Empty;
        if (policy == ImapUsernamePolicy.FullAddress)
            return normalized;

        var atIndex = normalized.IndexOf('@');
        return atIndex > 0 ? normalized[..atIndex] : normalized;
    }

    private static string GetDomain(string emailAddress)
    {
        var normalized = emailAddress?.Trim();
        var atIndex = normalized?.LastIndexOf('@') ?? -1;
        return atIndex >= 0 && atIndex < normalized.Length - 1 ? normalized[(atIndex + 1)..] : string.Empty;
    }
}

public sealed class EmbeddedKnownImapProviderCatalog : KnownImapProviderCatalog
{
    private const string ResourceName = "Wino.Services.Configuration.known-imap-providers.json";

    public EmbeddedKnownImapProviderCatalog(IKnownImapProviderCatalogLoader loader)
        : base(LoadEmbeddedDocument(loader))
    {
    }

    private static KnownImapProviderCatalogDocument LoadEmbeddedDocument(IKnownImapProviderCatalogLoader loader)
    {
        ArgumentNullException.ThrowIfNull(loader);

        using var source = typeof(EmbeddedKnownImapProviderCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException($"Embedded known IMAP provider catalog '{ResourceName}' was not found.");
        return loader.Load(source);
    }
}
