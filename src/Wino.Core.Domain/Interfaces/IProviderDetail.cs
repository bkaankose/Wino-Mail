using System.Collections.Generic;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Interfaces;

public interface IProviderDetail
{
    MailProviderType Type { get; }
    SpecialImapProvider SpecialImapProvider { get; }
    string Name { get; }
    string Description { get; }
    string ProviderImage { get; }
    bool IsSupported { get; }

    /// <summary>
    /// Featured providers get a tile on the first setup step. Others are listed in the catalog.
    /// </summary>
    bool IsFeatured { get; }

    /// <summary>
    /// Email domains the provider serves, used to find it by typing an address.
    /// </summary>
    IReadOnlyList<string> EmailDomains { get; }

    /// <summary>
    /// True when the query matches the name or one of the email domains, ignoring case.
    /// An empty query matches everything.
    /// </summary>
    bool MatchesSearch(string query);
}
