#nullable enable
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;

namespace Wino.Mail.ViewModels.Data;

/// <summary>
/// One alias row on the alias management page. The page reloads the whole list after every write,
/// so this item is a snapshot over its alias: it shapes the row's text and never mutates it in place.
/// </summary>
public sealed class AliasManagementItem(MailAccountAlias alias)
{
    private const string Separator = " • ";

    public MailAccountAlias Alias { get; } = alias;

    public string AliasAddress => Alias.AliasAddress;

    public bool IsPrimary => Alias.IsPrimary;

    /// <summary>The primary alias is what every other address falls back to, so it stays undeletable.</summary>
    public bool CanDelete => Alias.CanDelete && !Alias.IsPrimary;

    public bool CanSetPrimary => !Alias.IsPrimary;

    public bool IsCapabilityConfirmed => Alias.IsCapabilityConfirmed;

    public bool IsCapabilityUnknown => Alias.IsCapabilityUnknown;

    public bool IsCapabilityDenied => Alias.IsCapabilityDenied;

    public string StatusText => Alias.CapabilityDisplayName;

    /// <summary>
    /// What the status means for sending. A confirmed alias says everything in its badge, so it has no
    /// detail line; the other two states carry a consequence the badge alone cannot express.
    /// </summary>
    public string StatusDetailText => Alias.SendCapability switch
    {
        AliasSendCapability.Unknown => Translator.AccountAlias_Status_UnknownDetail,
        AliasSendCapability.Denied => Translator.AccountAlias_Status_DeniedDetail,
        _ => string.Empty
    };

    public bool HasStatusDetail => !string.IsNullOrEmpty(StatusDetailText);

    public string SourceText => Alias.SourceDisplayName;

    public string ReplyToText => string.IsNullOrWhiteSpace(Alias.ReplyToAddress)
        ? Translator.AccountAlias_ReplyToNotSet
        : string.Format(Translator.AccountAlias_ReplyToFormat, Alias.ReplyToAddress);

    public string SmimeSummaryText => Alias.IsSmimeEncryptionEnabled
        ? Translator.AccountAlias_SmimeOn
        : Translator.AccountAlias_SmimeOff;

    /// <summary>The collapsed row's second line: where the alias came from, where replies go, and its S/MIME state.</summary>
    public string DescriptionText => string.Join(Separator, (string[])[SourceText, ReplyToText, SmimeSummaryText]);

    public bool IsSmimeEncryptionEnabled => Alias.IsSmimeEncryptionEnabled;

    public ObservableCollection<X509Certificate2> Certificates => Alias.Certificates;

    public X509Certificate2 SelectedSigningCertificate
    {
        get => Alias.SelectedSigningCertificate;
        set => Alias.SelectedSigningCertificate = value;
    }

    /// <summary>A blank entry is always present, so a real choice needs more than one item.</summary>
    public bool HasCertificates => Certificates.Any(certificate => certificate is not null);

    public string CertificateDescriptionText => HasCertificates
        ? Translator.AccountAlias_SigningCertificate_Description
        : Translator.AccountAlias_SigningCertificate_NoneFound;

    /// <summary>Narrator reads the address with its status, because the badge colour carries no meaning on its own.</summary>
    public string RowAutomationName => IsPrimary
        ? string.Join(", ", (string[])[AliasAddress, Translator.AccountAlias_PrimaryBadge, StatusText])
        : string.Join(", ", (string[])[AliasAddress, StatusText]);

    public string RowAutomationId => $"AliasManagementRow_{AliasAddress}";

    public string MoreActionsAutomationId => $"AliasManagementMoreActions_{AliasAddress}";

    public string SigningCertificateAutomationId => $"AliasManagementSigningCertificate_{AliasAddress}";

    public string EncryptionAutomationId => $"AliasManagementEncryption_{AliasAddress}";

    public static IReadOnlyList<AliasManagementItem> Create(IEnumerable<MailAccountAlias> aliases)
        => aliases.Select(alias => new AliasManagementItem(alias)).ToArray();
}
