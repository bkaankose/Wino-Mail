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

    /// <summary>
    /// Finds app-password guidance for the address's domain, or null when the catalog has none.
    /// </summary>
    KnownAppPasswordHelp FindAppPasswordHelp(string emailAddress);

    /// <summary>
    /// Every provider the account setup offers: Outlook, Gmail, the catalog's setup-visible IMAP providers,
    /// generic IMAP and POP3, in display order.
    /// </summary>
    List<IProviderDetail> GetAvailableProviders();

    /// <summary>
    /// Returns the provider detail for the given type, or throws when the type is not offered.
    /// </summary>
    IProviderDetail GetProviderDetail(MailProviderType type);
}
