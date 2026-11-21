using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Mail.ViewModels.Common.Data;

public partial class AccountProviderDetailViewModel : ObservableObject, IAccountProviderDetailViewModel
{

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CapabilitySummary))]
    [NotifyPropertyChangedFor(nameof(DescriptionText))]
    [NotifyPropertyChangedFor(nameof(IsMailOn))]
    [NotifyPropertyChangedFor(nameof(IsCalendarOn))]
    [NotifyPropertyChangedFor(nameof(IsPeopleOn))]
    [NotifyPropertyChangedFor(nameof(IsToDoOn))]
    [NotifyPropertyChangedFor(nameof(AppsAutomationName))]
    public partial MailAccount Account { get; set; }

    public IProviderDetail ProviderDetail { get; set; }

    public Guid StartupEntityId => Account.Id;

    public string StartupEntityTitle => Account.Name;

    public int Order => Account.Order;

    public string StartupEntityAddresses => Account.DisplayAddress;
    public string CapabilitySummary => BuildCapabilitySummary(Account);
    public string DescriptionText => string.IsNullOrWhiteSpace(Account.DisplayAddress)
        ? CapabilitySummary
        : $"{CapabilitySummary} | {Account.DisplayAddress}";

    public bool IsMailOn => AppModeReadiness.IsModeEnabled(Account, WinoApplicationMode.Mail);
    public bool IsCalendarOn => AppModeReadiness.IsModeEnabled(Account, WinoApplicationMode.Calendar);
    public bool IsPeopleOn => AppModeReadiness.IsModeEnabled(Account, WinoApplicationMode.Contacts);
    public bool IsToDoOn => AppModeReadiness.IsModeEnabled(Account, WinoApplicationMode.Tasks);

    /// <summary>The app icons on the account row are read as one sentence, for example "Apps: Mail, Calendar".</summary>
    public string AppsAutomationName => string.Format(Translator.AccountApps_AppsOnAutomationName, CapabilitySummary);

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
