#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using MailCal.Core.Domain;
using MailCal.Core.Domain.Entities.Mail;
using MailCal.Core.Domain.Enums;
#if WINRT_EXPOSED
using WinRT;
#endif

namespace MailCal.Mail.ViewModels.Data;

/// <summary>
/// One alias row on the alias management page. The page reloads the whole list after every write,
/// so this item is a snapshot over its alias: it shapes the row's text and never mutates it in place.
/// </summary>
#if WINRT_EXPOSED
[GeneratedWinRTExposedType]
#endif
public sealed partial class AliasManagementItem(MailAccountAlias alias, bool smimeAvailable = true)
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
    public string DescriptionText => string.Join(Separator, IsSmimeAvailable
        ? (string[])[SourceText, ReplyToText, SmimeSummaryText]
        : (string[])[SourceText, ReplyToText]);

    public bool IsSmimeAvailable => smimeAvailable;
    public bool IsSmimeEncryptionEnabled => IsSmimeAvailable && Alias.IsSmimeEncryptionEnabled;

    /// <summary>A snapshot of the alias certificates, in a concrete type Native AOT can project as a vector.</summary>
    public AliasCertificateList Certificates { get; } = new(alias.Certificates);

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

    public static AliasManagementItemList Create(IEnumerable<MailAccountAlias> aliases, bool smimeAvailable = true)
        => new(aliases.Select(alias => new AliasManagementItem(alias, smimeAvailable)));
}

/// <summary>
/// The alias rows bound to the page's list. Native AOT requires a concrete, WinRT-exposed collection type:
/// an array or generic list of a non-WinRT type has no generated vector vtable, so the list control rejects it.
/// </summary>
#if WINRT_EXPOSED
[GeneratedWinRTExposedType]
#endif
public sealed partial class AliasManagementItemList(IEnumerable<AliasManagementItem> items) : List<AliasManagementItem>(items);

/// <summary>The signing certificate choices of one alias row, with the leading blank entry. See <see cref="AliasManagementItemList"/>.</summary>
#if WINRT_EXPOSED
[GeneratedWinRTExposedType]
#endif
public sealed partial class AliasCertificateList(IEnumerable<X509Certificate2> certificates) : List<X509Certificate2>(certificates);
