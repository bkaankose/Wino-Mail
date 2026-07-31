using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Extensions;

namespace Wino.Mail.ViewModels.Data;

public partial class AccountStorageItemViewModel(MailAccount account, long sizeBytes, ICommand deleteAllCommand, ICommand deleteOneMonthCommand, ICommand deleteThreeMonthsCommand, ICommand deleteSixMonthsCommand, ICommand deleteYearCommand) : ObservableObject
{
    public MailAccount Account { get; } = account;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    public partial long SizeBytes { get; set; } = sizeBytes;

    [ObservableProperty]
    public partial string SizeDescription { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ICommand DeleteAllCommand { get; set; } = deleteAllCommand;

    [ObservableProperty]
    public partial ICommand DeleteOneMonthCommand { get; set; } = deleteOneMonthCommand;

    [ObservableProperty]
    public partial ICommand DeleteThreeMonthsCommand { get; set; } = deleteThreeMonthsCommand;

    [ObservableProperty]
    public partial ICommand DeleteSixMonthsCommand { get; set; } = deleteSixMonthsCommand;

    [ObservableProperty]
    public partial ICommand DeleteYearCommand { get; set; } = deleteYearCommand;

    public string AccountName => string.IsNullOrWhiteSpace(Account.Name) ? Account.Address ?? string.Empty : Account.Name;
    public string AccountAddress => Account.Address ?? string.Empty;
    public string SizeText => SizeBytes.GetBytesReadable();
}
