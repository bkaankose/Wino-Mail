using System.Collections.Generic;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Connectivity;

public sealed record KnownImapProviderCatalogDocument
{
    public int SchemaVersion { get; init; }
    public IReadOnlyList<KnownImapProviderDefinition> Providers { get; init; } = [];
    public IReadOnlyList<KnownImapFolderAlias> GenericFolderAliases { get; init; } = [];
}

public sealed record KnownImapProviderDefinition
{
    public string Id { get; init; } = string.Empty;
    public SpecialImapProvider SpecialImapProvider { get; init; }
    public bool SetupVisible { get; init; }
    public int SetupOrder { get; init; }
    public IReadOnlyList<string> EmailDomains { get; init; } = [];
    public IReadOnlyList<string> IncomingHosts { get; init; } = [];
    public KnownImapServerDefinition Incoming { get; init; } = new();
    public KnownImapServerDefinition Outgoing { get; init; } = new();
    public int MaxConcurrentClients { get; init; } = 5;
    public ImapConnectionPolicyVersion ConnectionPolicyVersion { get; init; } = ImapConnectionPolicyVersion.Corrected;
    public string CalDavServiceUrl { get; init; } = string.Empty;
    public string CardDavServiceUrl { get; init; } = string.Empty;
    public string AppPasswordHelpUrl { get; init; } = string.Empty;
    public IReadOnlyList<KnownImapFolderAlias> FolderAliases { get; init; } = [];
}

public sealed record KnownImapServerDefinition
{
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; }
    public ImapConnectionSecurity Security { get; init; }
    public ImapAuthenticationMethod Authentication { get; init; }
    public ImapUsernamePolicy UsernamePolicy { get; init; }
}

public sealed record KnownImapFolderAlias
{
    public SpecialFolderType Role { get; init; }
    public string Value { get; init; } = string.Empty;
    public bool MatchFullPath { get; init; }
}
