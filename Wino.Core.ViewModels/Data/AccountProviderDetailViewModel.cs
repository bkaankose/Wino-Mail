using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.ViewModels.Data;

public partial class AccountProviderDetailViewModel : ObservableObject, IAccountProviderDetailViewModel
{

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CapabilitySummary))]
    [NotifyPropertyChangedFor(nameof(DescriptionText))]
    private MailAccount account;

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

    public bool HasProfilePicture => !string.IsNullOrEmpty(Account.Base64ProfilePictureData);

    public AccountProviderDetailViewModel(IProviderDetail providerDetail, MailAccount account)
    {
        ProviderDetail = providerDetail;
        Account = account;
    }

    private static string BuildCapabilitySummary(MailAccount account)
    {
        if (account?.IsMailAccessGranted == true && account.IsCalendarAccessGranted)
            return Translator.AccountCapability_MailAndCalendar;

        if (account?.IsMailAccessGranted == true)
            return Translator.AccountCapability_MailOnly;

        return Translator.AccountCapability_CalendarOnly;
    }
}
