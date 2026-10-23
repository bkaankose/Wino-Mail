using System;
using CommunityToolkit.Mvvm.ComponentModel;
using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Domain.Models.Accounts;

namespace MailCal.Mail.ViewModels.Data;

public partial class AccountProviderDetailViewModel : ObservableObject, IAccountProviderDetailViewModel
{

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CapabilitySummary))]
    [NotifyPropertyChangedFor(nameof(DescriptionText))]
    public partial MailAccount Account { get; set; }

    public IProviderDetail ProviderDetail { get; set; }

    public Guid StartupEntityId => Account.Id;

    public string StartupEntityTitle => Account.Name;

    public int Order => Account.Order;

    public string StartupEntityAddresses => Account.Address;
    public string CapabilitySummary => BuildCapabilitySummary(Account);
    public string DescriptionText => string.IsNullOrWhiteSpace(Account.Address)
        ? CapabilitySummary
        : $"{CapabilitySummary} | {Account.Address}";

    public int HoldingAccountCount => 1;

    public bool HasProfilePicture => Account.ProfilePictureFileId.HasValue;

    public AccountProviderDetailViewModel(IProviderDetail providerDetail, MailAccount account)
    {
        ProviderDetail = providerDetail;
        Account = account;
    }

    private static string BuildCapabilitySummary(MailAccount account)
        => AccountCapabilitySummary.Build(account);
}
