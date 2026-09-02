using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Connectivity;

namespace Wino.Services;

public sealed class KnownImapProviderCatalogLoader : IKnownImapProviderCatalogLoader
{
    public const int SupportedSchemaVersion = 1;

    public KnownImapProviderCatalogDocument Load(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);

        KnownImapProviderCatalogDocument document;
        try
        {
            document = JsonSerializer.Deserialize(source, KnownImapProviderCatalogJsonContext.Default.KnownImapProviderCatalogDocument);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The known IMAP provider catalog is not valid JSON.", ex);
        }

        Validate(document);
        return document;
    }

    internal static void Validate(KnownImapProviderCatalogDocument document)
    {
        if (document == null)
            throw new InvalidDataException("The known IMAP provider catalog is empty.");

        if (document.SchemaVersion != SupportedSchemaVersion)
            throw new InvalidDataException($"Unsupported known IMAP provider catalog schema version {document.SchemaVersion}.");

        if (document.Providers == null || document.GenericFolderAliases == null)
            throw new InvalidDataException("The known IMAP provider catalog contains null collections.");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var providers = new HashSet<SpecialImapProvider>();
        var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var provider in document.Providers ?? [])
        {
            if (provider.EmailDomains == null || provider.IncomingHosts == null || provider.FolderAliases == null)
                throw new InvalidDataException($"Known IMAP provider '{provider.Id}' contains null collections.");

            if (string.IsNullOrWhiteSpace(provider.Id) || !ids.Add(provider.Id.Trim()))
                throw new InvalidDataException($"Known IMAP provider ID '{provider.Id}' is empty or duplicated.");

            ValidateEnum(provider.SpecialImapProvider, $"provider '{provider.Id}' special provider");
            if (provider.SpecialImapProvider == SpecialImapProvider.None || !providers.Add(provider.SpecialImapProvider))
                throw new InvalidDataException($"Known IMAP provider '{provider.Id}' has an invalid or duplicated special provider.");

            if (provider.SetupOrder < 0 ||
                ((provider.EmailDomains?.Count ?? 0) == 0 && (provider.IncomingHosts?.Count ?? 0) == 0))
                throw new InvalidDataException($"Known IMAP provider '{provider.Id}' has invalid setup order or no matchers.");

            foreach (var domain in provider.EmailDomains ?? [])
                ValidateMatcher(domain, domains, "email domain", provider.Id);

            foreach (var host in provider.IncomingHosts ?? [])
                ValidateMatcher(host, hosts, "incoming host", provider.Id);

            ValidateServer(provider.Incoming, "incoming", provider.Id);
            ValidateServer(provider.Outgoing, "outgoing", provider.Id);

            if (provider.MaxConcurrentClients is < 1 or > 100)
                throw new InvalidDataException($"Known IMAP provider '{provider.Id}' has an invalid concurrency value.");

            ValidateEnum(provider.ConnectionPolicyVersion, $"provider '{provider.Id}' connection policy");
            ValidateOptionalAbsoluteUrl(provider.CalDavServiceUrl, "CalDAV", provider.Id);
            ValidateOptionalAbsoluteUrl(provider.CardDavServiceUrl, "CardDAV", provider.Id);
            ValidateOptionalAbsoluteUrl(provider.AppPasswordHelpUrl, "app-password help", provider.Id);
            ValidateAliases(provider.FolderAliases, $"provider '{provider.Id}'", allowFullPath: true);
        }

        ValidateAliases(document.GenericFolderAliases, "generic aliases", allowFullPath: false);
    }

    private static void ValidateMatcher(string value, HashSet<string> seen, string kind, string providerId)
    {
        if (string.IsNullOrWhiteSpace(value) || !seen.Add(value.Trim()))
            throw new InvalidDataException($"Known IMAP provider '{providerId}' has an empty or duplicated {kind} '{value}'.");
    }

    private static void ValidateServer(KnownImapServerDefinition server, string kind, string providerId)
    {
        if (server == null || string.IsNullOrWhiteSpace(server.Host) || server.Port is < 1 or > 65535)
            throw new InvalidDataException($"Known IMAP provider '{providerId}' has invalid {kind} server settings.");

        ValidateEnum(server.Security, $"provider '{providerId}' {kind} security");
        ValidateEnum(server.Authentication, $"provider '{providerId}' {kind} authentication");
        ValidateEnum(server.UsernamePolicy, $"provider '{providerId}' {kind} username policy");
    }

    private static void ValidateAliases(
        IReadOnlyList<KnownImapFolderAlias> aliases,
        string owner,
        bool allowFullPath)
    {
        var seen = new Dictionary<(string Value, bool FullPath), SpecialFolderType>();
        foreach (var alias in aliases ?? [])
        {
            ValidateEnum(alias.Role, $"{owner} folder role");
            if (!IsSupportedFolderRole(alias.Role) ||
                string.IsNullOrWhiteSpace(alias.Value) ||
                (!allowFullPath && alias.MatchFullPath))
                throw new InvalidDataException($"{owner} contains an invalid folder alias.");

            var key = (alias.Value.Trim().ToUpperInvariant(), alias.MatchFullPath);
            if (seen.TryGetValue(key, out var existingRole) && existingRole != alias.Role)
                throw new InvalidDataException($"{owner} maps folder alias '{alias.Value}' to conflicting roles.");

            seen[key] = alias.Role;
        }
    }

    private static bool IsSupportedFolderRole(SpecialFolderType role)
        => role is SpecialFolderType.Inbox or
            SpecialFolderType.Draft or
            SpecialFolderType.Sent or
            SpecialFolderType.Deleted or
            SpecialFolderType.Junk or
            SpecialFolderType.Archive or
            SpecialFolderType.Important or
            SpecialFolderType.Starred;

    private static void ValidateOptionalAbsoluteUrl(string value, string kind, string providerId)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
        {
            throw new InvalidDataException($"Known IMAP provider '{providerId}' has an invalid {kind} URL.");
        }
    }

    private static void ValidateEnum<T>(T value, string description) where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
            throw new InvalidDataException($"Known IMAP catalog contains an invalid {description} value '{value}'.");
    }
}
