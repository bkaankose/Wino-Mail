using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.ViewModels.Data;

namespace Wino.Core.ViewModels.Data;

/// <summary>
/// An account group in the Manage accounts list, holding its accounts and linked accounts in layout order.
/// It is only listed there; startup and account pickers keep listing the accounts themselves.
/// </summary>
public partial class AccountGroupProviderDetailViewModel : ObservableObject, IAccountProviderDetailViewModel
{
    public AccountGroupProviderDetailViewModel(MailAccountGroup group, List<IAccountProviderDetailViewModel> entries)
    {
        Group = group;
        Entries = entries;
    }

    public MailAccountGroup Group { get; }

    public List<IAccountProviderDetailViewModel> Entries { get; }

    public MailAccount Account => null;

    public string Name => Group.Name ?? string.Empty;

    public string? ColorKey => AccountGroupColors.Normalize(Group.ColorKey);

    public string DescriptionText => AccountGroupPresentation.GetAccountCountText(HoldingAccountCount);

    public Guid StartupEntityId => Group.Id;

    public string StartupEntityTitle => Name;

    public string StartupEntityAddresses => string.Join(", ", Entries.Select(entry => entry.StartupEntityAddresses));

    public int Order => 0;

    public IProviderDetail ProviderDetail { get; set; }

    public int HoldingAccountCount => Entries.Sum(entry => entry.HoldingAccountCount);
}
