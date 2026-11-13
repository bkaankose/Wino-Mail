using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.ViewModels.Data;

namespace Wino.Core.ViewModels.Data;

public partial class MergedAccountProviderDetailViewModel : ObservableObject, IAccountProviderDetailViewModel
{
    public MailAccount Account => null;

    public List<AccountProviderDetailViewModel> HoldingAccounts { get; }
    public MergedInbox MergedInbox { get; }

    public string AccountAddresses => string.Join(", ", HoldingAccounts.Select(a => a.Account.Address));

    public Guid StartupEntityId => MergedInbox.Id;

    public string StartupEntityTitle => MergedInbox.Name;

    public int Order => 0;

    public IProviderDetail ProviderDetail { get; set; }

    public string StartupEntityAddresses => AccountAddresses;

    public int HoldingAccountCount => HoldingAccounts.Count;

    public string? ColorKey => AccountGroupColors.Normalize(MergedInbox?.ColorKey);

    public WinoIconGlyph IconGlyph => AccountGroupIcons.GetGlyph(MergedInbox?.Icon ?? AccountGroupIcon.PeopleLink);

    /// <summary>"Account group · N accounts", with the folder behavior when the folders are combined.</summary>
    public string DescriptionText => MergedInbox?.CombineFolders == true
        ? $"{AccountGroupPresentation.GetAccountCountText(HoldingAccountCount)} · {Translator.AccountGroup_CombinedFolders}"
        : AccountGroupPresentation.GetAccountCountText(HoldingAccountCount);

    public MergedAccountProviderDetailViewModel(MergedInbox mergedInbox, List<AccountProviderDetailViewModel> holdingAccounts)
    {
        MergedInbox = mergedInbox;
        HoldingAccounts = holdingAccounts;
    }
}
