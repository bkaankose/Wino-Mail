#nullable enable
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Mail.ViewModels;

/// <summary>
/// How much mail an IMAP or POP3 account downloads first, chosen on the Features step when Advanced setup opens this
/// page before the apps were chosen. The account is created from the wizard context, so the choice is written there.
/// </summary>
public partial class ImapCalDavSettingsPageViewModel
{
    public List<InitialSynchronizationRangeOption> InitialSynchronizationRanges { get; } =
    [
        new(InitialSynchronizationRange.ThreeMonths, Translator.AccountCreation_InitialSynchronization_3Months),
        new(InitialSynchronizationRange.SixMonths, Translator.AccountCreation_InitialSynchronization_6Months),
        new(InitialSynchronizationRange.NineMonths, Translator.AccountCreation_InitialSynchronization_9Months),
        new(InitialSynchronizationRange.OneYear, Translator.AccountCreation_InitialSynchronization_Year),
        new(InitialSynchronizationRange.Everything, Translator.AccountCreation_InitialSynchronization_Everything)
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInitialSynchronizationWarningVisible))]
    public partial InitialSynchronizationRangeOption? SelectedInitialSynchronizationRange { get; set; }

    public string MailRangeDescription => IsPop3
        ? Translator.ProviderSelection_MailRangeDescriptionPop3
        : Translator.ProviderSelection_MailRangeDescription;

    public bool IsInitialSynchronizationWarningVisible => SelectedInitialSynchronizationRange?.IsEverything == true;

    private void ApplyInitialSynchronizationRangeFromWizard()
    {
        var range = _wizardContext.SelectedInitialSynchronizationRange;
        SelectedInitialSynchronizationRange = InitialSynchronizationRanges.FirstOrDefault(option => option.Range == range)
            ?? InitialSynchronizationRanges.First(option => option.Range == InitialSynchronizationRange.SixMonths);
        OnPropertyChanged(nameof(MailRangeDescription));
    }

    partial void OnSelectedInitialSynchronizationRangeChanged(InitialSynchronizationRangeOption? value)
    {
        if (value != null && IsCreateMode)
            _wizardContext.SelectedInitialSynchronizationRange = value.Range;
    }
}
