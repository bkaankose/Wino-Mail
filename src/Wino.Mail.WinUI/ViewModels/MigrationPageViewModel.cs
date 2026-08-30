using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Migration;

namespace Wino.Mail.WinUI.ViewModels;

public sealed partial class MigrationAccountOptionViewModel : ObservableObject
{
    public Guid AccountId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string Address { get; init; } = string.Empty;
    public Wino.Core.Domain.Enums.MailProviderType ProviderType { get; init; }
    [ObservableProperty]
    public partial bool EnableContacts { get; set; }

    [ObservableProperty]
    public partial bool EnableTasks { get; set; }

    [ObservableProperty]
    public partial bool EnableMailFilters { get; set; }

    public MigrationAccountOptions ToOptions() => new(
        AccountId,
        DisplayName,
        Address,
        ProviderType,
        EnableContacts,
        EnableTasks,
        EnableMailFilters,
        DeferSignIn: true);
}

public sealed partial class MigrationStepItemViewModel : ObservableObject
{
    public MigrationStepKind Kind { get; init; }
    public string Title { get; init; } = string.Empty;

    [ObservableProperty]
    public partial string Description { get; set; } = string.Empty;

    [ObservableProperty]
    public partial MigrationStepStatus Status { get; set; }
}

public sealed partial class MigrationPageViewModel : ObservableObject, IDisposable
{
    private readonly IMigrationCoordinator _coordinator;
    private DispatcherQueue? _dispatcherQueue;

    public ObservableCollection<MigrationAccountOptionViewModel> Accounts { get; } = [];
    public ObservableCollection<MigrationStepItemViewModel> Steps { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial bool IsReady { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartFreshCommand))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
    public partial bool IsFailed { get; set; }

    [ObservableProperty]
    public partial bool IsCompleted { get; set; }

    [ObservableProperty]
    public partial string CurrentTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CurrentDescription { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TechnicalDetails { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CompletionSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double Progress { get; set; }

    public bool IsOptionsVisible => IsReady && !IsRunning && !IsFailed && !IsCompleted;
    public bool IsProgressVisible => IsRunning;
    public bool IsFailureVisible => IsFailed;
    public bool IsSuccessVisible => IsCompleted;
    public bool CanSkipMigration => IsFailed && !IsRunning;

    public string StepProgressText => string.Format(
        Wino.Core.Domain.Translator.Migration_StepProgress,
        Math.Clamp(Steps.Count(step => step.Status is MigrationStepStatus.Completed) + 1, 1, Math.Max(Steps.Count, 1)),
        Steps.Count);

    public MigrationPageViewModel(IMigrationCoordinator coordinator)
    {
        _coordinator = coordinator;
        _coordinator.ProgressChanged += OnProgressChanged;
    }

    public void InitializeDispatcher(DispatcherQueue dispatcherQueue) => _dispatcherQueue = dispatcherQueue;

    public async Task InitializeAsync()
    {
        var plan = await _coordinator.InspectAsync();

        Accounts.Clear();
        foreach (var account in plan.Accounts)
        {
            Accounts.Add(new MigrationAccountOptionViewModel
            {
                AccountId = account.AccountId,
                DisplayName = account.DisplayName,
                Address = account.Address,
                ProviderType = account.ProviderType,
                EnableContacts = true,
                EnableTasks = true,
                EnableMailFilters = true
            });
        }

        EnsureSteps();
        CurrentTitle = Wino.Core.Domain.Translator.Migration_RequiredTitle;
        CurrentDescription = Wino.Core.Domain.Translator.Migration_SimpleIntroDescription;
        IsReady = true;
        NotifyStateChanged();
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartAsync() => RunMigrationAsync();

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private Task RetryAsync() => RunMigrationAsync();

    [RelayCommand(CanExecute = nameof(CanStartFresh))]
    private async Task StartFreshAsync()
    {
        BeginOperation();

        var result = await _coordinator.StartFreshAsync();
        ApplyResult(result);
    }

    private bool CanStart() => IsReady && !IsRunning;
    private bool CanRetry() => IsFailed && !IsRunning;
    private bool CanStartFresh() => !IsRunning && !IsCompleted;

    private async Task RunMigrationAsync()
    {
        BeginOperation();
        var result = await _coordinator.RunAsync(Accounts.Select(account => account.ToOptions()).ToArray());
        ApplyResult(result);
    }

    private void BeginOperation()
    {
        IsFailed = false;
        IsCompleted = false;
        IsRunning = true;
        ErrorMessage = string.Empty;
        TechnicalDetails = string.Empty;
        Progress = 0;
        CurrentTitle = Wino.Core.Domain.Translator.Migration_WorkingTitle;
        CurrentDescription = Wino.Core.Domain.Translator.Migration_WorkingDescription;
        NotifyStateChanged();
    }

    private void ApplyResult(MigrationResult result)
    {
        IsRunning = false;
        IsCompleted = result.Status is MigrationStatus.Completed or MigrationStatus.Skipped;
        IsFailed = !IsCompleted;

        if (IsCompleted)
        {
            CurrentTitle = Wino.Core.Domain.Translator.Migration_SuccessTitle;
            CurrentDescription = Wino.Core.Domain.Translator.Migration_SimpleSuccessDescription;
            CompletionSummary = $"{result.AccountCount} {Wino.Core.Domain.Translator.Migration_Accounts} · " +
                                $"{result.MailCount} {Wino.Core.Domain.Translator.Migration_Messages} · " +
                                $"{result.CalendarCount} {Wino.Core.Domain.Translator.Migration_CalendarRecords}";
            Progress = 1;
        }
        else
        {
            CurrentTitle = Wino.Core.Domain.Translator.Migration_FailedTitle;
            CurrentDescription = Wino.Core.Domain.Translator.Migration_SimpleFailedDescription;
            ErrorMessage = result.ErrorMessage ?? "Migration did not complete.";
            TechnicalDetails = BuildTechnicalDetails(result);
        }

        NotifyStateChanged();
    }

    private void OnProgressChanged(object? sender, MigrationProgress progress)
    {
        void Apply()
        {
            Progress = Math.Clamp(progress.Progress, 0, 1);

            var step = Steps.FirstOrDefault(item => item.Kind == progress.Step);
            if (step != null)
            {
                step.Status = progress.Status;
                step.Description = progress.Description;
            }

            OnPropertyChanged(nameof(StepProgressText));
        }

        if (_dispatcherQueue?.HasThreadAccess == false)
            _dispatcherQueue.TryEnqueue(Apply);
        else
            Apply();
    }

    private void EnsureSteps()
    {
        if (Steps.Count > 0)
            return;

        AddStep(MigrationStepKind.CheckExistingData, "Check existing data");
        AddStep(MigrationStepKind.ChooseFeatures, "Choose account features");
        AddStep(MigrationStepKind.PrepareDatabase, "Prepare the new database");
        AddStep(MigrationStepKind.MigrateAccountsAndSettings, "Move accounts and settings");
        AddStep(MigrationStepKind.MigrateMailAndFiles, "Move mail and local files");
        AddStep(MigrationStepKind.MigrateCalendars, "Move calendars and events");
        AddStep(MigrationStepKind.ReconnectAccounts, "Reconnect accounts or sign in later");
        AddStep(MigrationStepKind.ConfigureFeatures, "Configure account features");
        AddStep(MigrationStepKind.ValidateAndFinalize, "Validate and finish");
        AddStep(MigrationStepKind.Completed, "Ready to launch");
    }

    private void AddStep(MigrationStepKind kind, string title) => Steps.Add(new MigrationStepItemViewModel
    {
        Kind = kind,
        Title = title,
        Description = "Waiting to start",
        Status = MigrationStepStatus.Pending
    });

    private static string BuildTechnicalDetails(MigrationResult result)
        => $"Step: {result.FailedStep?.ToString() ?? "Unknown"}\nStatus: {result.Status}";

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(IsOptionsVisible));
        OnPropertyChanged(nameof(IsProgressVisible));
        OnPropertyChanged(nameof(IsFailureVisible));
        OnPropertyChanged(nameof(IsSuccessVisible));
        OnPropertyChanged(nameof(CanSkipMigration));
        OnPropertyChanged(nameof(StepProgressText));
    }

    public void Dispose() => _coordinator.ProgressChanged -= OnProgressChanged;
}
