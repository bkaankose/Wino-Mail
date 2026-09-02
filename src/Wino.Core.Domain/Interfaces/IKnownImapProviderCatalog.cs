using System.Collections.Generic;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Connectivity;

namespace Wino.Core.Domain.Interfaces;

public interface IKnownImapProviderCatalog
{
    int SchemaVersion { get; }
    IReadOnlyList<KnownImapProviderDefinition> Providers { get; }
    IReadOnlyList<KnownImapProviderDefinition> SetupProviders { get; }
    IReadOnlyList<KnownImapFolderAlias> GenericFolderAliases { get; }

    KnownImapProviderDefinition GetBySpecialProvider(SpecialImapProvider provider);
    KnownImapProviderDefinition Match(string emailAddress, string incomingHost, SpecialImapProvider preferredProvider = SpecialImapProvider.None);
    string ResolveUsername(ImapUsernamePolicy policy, string emailAddress);
}
