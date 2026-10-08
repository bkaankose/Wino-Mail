using System;
using System.Collections.ObjectModel;
using System.Security.Cryptography.X509Certificates;
using SQLite;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Entities.Mail;

public class RemoteAccountAlias
{
    /// <summary>
    /// Display address of the alias.
    /// </summary>
    public string AliasAddress { get; set; }

    /// <summary>
    /// Address to be included in Reply-To header when alias is used for sending messages.
    /// </summary>
    public string ReplyToAddress { get; set; }

    /// <summary>
    /// Whether this alias is the primary alias for the account.
    /// </summary>
    public bool IsPrimary { get; set; }

    /// <summary>
    /// Whether the alias is verified by the server.
    /// Only Gmail aliases are verified for now.
    /// Non-verified alias messages might be rejected by SMTP server.
    /// </summary>
    public bool IsVerified { get; set; }

    /// <summary>
    /// Whether this alias is the root alias for the account.
    /// Root alias means the first alias that was created for the account.
    /// It can't be deleted or changed.
    /// </summary>
    public bool IsRootAlias { get; set; }

    /// <summary>
    /// Optional sender name for the alias.
    /// Falls back to account's sender name if not set when preparing messages.
    /// Used for Gmail only.
    /// </summary>
    public string AliasSenderName { get; set; }

    /// <summary>
    /// Whether the alias was entered by the user or discovered from the provider.
    /// </summary>
    public AliasSource Source { get; set; } = AliasSource.Manual;

    /// <summary>
    /// Represents Wino's confidence that the alias can be used for sending.
    /// </summary>
    public AliasSendCapability SendCapability { get; set; } = AliasSendCapability.Unknown;
}

public class MailAccountAlias : RemoteAccountAlias
{
    /// <summary>
    /// Unique Id for the alias.
    /// </summary>
    [PrimaryKey]
    public Guid Id { get; set; }

    /// <summary>
    /// Account id that this alias is attached to.
    /// </summary>
    public Guid AccountId { get; set; }

    /// <summary>
    /// Root aliases can't be deleted.
    /// </summary>
    public bool CanDelete => !IsRootAlias;

    public string SelectedSigningCertificateThumbprint { get; set; }
    public bool IsSmimeEncryptionEnabled { get; set; }

    [Ignore]
    public X509Certificate2 SelectedSigningCertificate { get; set; }

    [Ignore]
    public ObservableCollection<X509Certificate2> Certificates { get; set; } = [];

    [Ignore]
    public bool IsCapabilityConfirmed => SendCapability == AliasSendCapability.Confirmed;

    [Ignore]
    public bool IsCapabilityUnknown => SendCapability == AliasSendCapability.Unknown;

    [Ignore]
    public bool IsCapabilityDenied => SendCapability == AliasSendCapability.Denied;

    [Ignore]
    public string CapabilityDisplayName => SendCapability switch
    {
        AliasSendCapability.Confirmed => Translator.AccountAlias_Status_Confirmed,
        AliasSendCapability.Denied => Translator.AccountAlias_Status_Denied,
        _ => Translator.AccountAlias_Status_Unknown
    };

    [Ignore]
    public string SourceDisplayName => Source switch
    {
        AliasSource.ProviderDiscovered => Translator.AccountAlias_Source_ProviderDiscovered,
        _ => Translator.AccountAlias_Source_Manual
    };
}
