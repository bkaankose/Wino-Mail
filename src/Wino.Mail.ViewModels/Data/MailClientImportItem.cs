using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Presentation;

namespace Wino.Mail.ViewModels.Data;

/// <summary>One account row of the import dialog. An account Wino already has is shown but cannot be picked.</summary>
public sealed partial class MailClientImportItem : ObservableObject
{
    public MailClientImportItem(MailClientImportCandidate candidate)
    {
        Candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
        IsSelected = !candidate.IsAlreadyAdded;
    }

    public MailClientImportCandidate Candidate { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public bool CanSelect => !Candidate.IsAlreadyAdded;

    /// <summary>The provider's brand glyph; POP3 has none of its own and uses the IMAP one.</summary>
    public WinoIconGlyph IconGlyph
        => MailProviderGlyphs.GetGlyph(Candidate.ProviderType, Candidate.SpecialImapProvider) is var glyph && glyph != WinoIconGlyph.None
            ? glyph
            : WinoIconGlyph.IMAP;

    public override string ToString() => Title;

    public string Title
        => string.IsNullOrWhiteSpace(Candidate.AccountName)
           || string.Equals(Candidate.AccountName, Candidate.Address, StringComparison.OrdinalIgnoreCase)
            ? Candidate.Address
            : string.Format(Translator.MailClientImport_RowTitle, Candidate.AccountName, Candidate.Address);

    public string Subtitle
        => Candidate.IsAlreadyAdded
            ? string.Format(Translator.MailClientImport_RowSubtitle, GetSourceName(Candidate.Source), Translator.MailClientImport_AlreadyAdded)
            : string.Format(Translator.MailClientImport_RowSubtitle, GetSourceName(Candidate.Source), GetTypeName(Candidate));

    public static string GetSourceName(MailClientImportSource source) => source switch
    {
        MailClientImportSource.Thunderbird => Translator.MailClientImport_Source_Thunderbird,
        MailClientImportSource.OutlookClassic => Translator.MailClientImport_Source_OutlookClassic,
        MailClientImportSource.NewOutlook => Translator.MailClientImport_Source_NewOutlook,
        MailClientImportSource.Evolution => Translator.MailClientImport_Source_Evolution,
        MailClientImportSource.GnomeOnlineAccounts => Translator.MailClientImport_Source_GnomeOnlineAccounts,
        _ => source.ToString()
    };

    private static string GetTypeName(MailClientImportCandidate candidate) => candidate.ProviderType switch
    {
        MailProviderType.Outlook => Translator.MailClientImport_Type_Outlook,
        MailProviderType.Gmail => Translator.MailClientImport_Type_Gmail,
        MailProviderType.POP3 => Translator.MailClientImport_Type_Pop3,
        MailProviderType.Exchange => GetExchangeTypeName(candidate.ServerInformation?.ExchangeTransport ?? ExchangeTransport.Automatic),
        _ => Translator.MailClientImport_Type_Imap
    };

    private static string GetExchangeTypeName(ExchangeTransport transport) => transport switch
    {
        ExchangeTransport.MapiHttp => string.Format(Translator.MailClientImport_RowSubtitle, Translator.MailClientImport_Type_Exchange, Translator.ExchangeSetup_TransportMapi),
        ExchangeTransport.Ews => Translator.ExchangeSetup_TransportEws,
        ExchangeTransport.ActiveSync => Translator.ExchangeSetup_TransportActiveSync,
        _ => Translator.MailClientImport_Type_Exchange
    };
}
