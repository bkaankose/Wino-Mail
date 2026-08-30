using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Migration;
using Wino.Services;

namespace Wino.Mail.WinUI.ViewModels;

public sealed partial class MigrationAccountOptionViewModel : ObservableObject
{
    public Guid AccountId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string Address { get; init; } = string.Empty;
    public Wino.Core.Domain.Enums.MailProviderType ProviderType { get; init; }

    public string ProviderIconPath => ProviderType switch
    {
        MailProviderType.Gmail => "ms-appx:///Assets/Providers/Gmail.png",
        MailProviderType.Outlook => "ms-appx:///Assets/Providers/Outlook.png",
        _ => "ms-appx:///Assets/Providers/IMAP4.png"
    };

    public string AuthorizationFeaturesText
    {
        get
        {
            var features = new List<string>();
            if (EnableContacts)
                features.Add(Wino.Core.Domain.Translator.Migration_Contacts);
            if (EnableTasks)
                features.Add(Wino.Core.Domain.Translator.Migration_ToDo);
            if (EnableMailFilters)
                features.Add(Wino.Core.Domain.Translator.Migration_MailFilters);

            return string.Format(
                Wino.Core.Domain.Translator.Migration_AuthorizationPermissions,
                string.Join(", ", features));
        }
    }
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
    private readonly IDatabaseService _databaseService;
    private readonly IMigrationAccountAuthorizationService _authorizationService;
    private readonly IWinoLogger _logger;
    private readonly Queue<MigrationAccountOptionViewModel> _authorizationQueue = new();
    private DispatcherQueue? _dispatcherQueue;
    private MigrationResult? _lastResult;
    private int _authorizationAccountCount;

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

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AuthenticateCommand))]
    [NotifyCanExecuteChangedFor(nameof(SkipAuthenticationCommand))]
    public partial MigrationAccountOptionViewModel? CurrentAuthorizationAccount { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AuthenticateCommand))]
    [NotifyCanExecuteChangedFor(nameof(SkipAuthenticationCommand))]
    public partial bool IsAuthorizing { get; set; }

    [ObservableProperty]
    public partial string AuthorizationErrorMessage { get; set; } = string.Empty;

    public bool IsOptionsVisible => IsReady && !IsRunning && !IsFailed && !IsCompleted;
    public bool IsProgressVisible => IsRunning;
    public bool IsFailureVisible => IsFailed;
    public bool IsSuccessVisible => IsCompleted;
    public bool IsAuthorizationVisible => CurrentAuthorizationAccount != null && !IsCompleted;
    public bool IsAuthorizationErrorVisible => IsAuthorizationVisible && !string.IsNullOrWhiteSpace(AuthorizationErrorMessage);
    public bool CanSkipMigration => IsFailed && !IsRunning;

    public string AuthorizationProgressText => string.Format(
        Wino.Core.Domain.Translator.Migration_AuthorizationProgress,
        Math.Max(1, _authorizationAccountCount - _authorizationQueue.Count),
        _authorizationAccountCount);

    public string StepProgressText => string.Format(
        Wino.Core.Domain.Translator.Migration_StepProgress,
        Math.Clamp(Steps.Count(step => step.Status is MigrationStepStatus.Completed) + 1, 1, Math.Max(Steps.Count, 1)),
        Steps.Count);

    public MigrationPageViewModel(
        IMigrationCoordinator coordinator,
        IDatabaseService databaseService,
        IMigrationAccountAuthorizationService authorizationService,
        IWinoLogger logger)
    {
        _coordinator = coordinator;
        _databaseService = databaseService;
        _authorizationService = authorizationService;
        _logger = logger;
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

        if (plan.Status == MigrationStatus.AwaitingUser)
        {
            await BeginAuthorizationAsync(Accounts);
            return;
        }

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
        await ApplyResultAsync(result, []);
    }

    private bool CanStart() => IsReady && !IsRunning;
    private bool CanRetry() => IsFailed && !IsRunning;
    private bool CanStartFresh() => !IsRunning && !IsCompleted;

    private async Task RunMigrationAsync()
    {
        BeginOperation();
        var selectedAccounts = Accounts.Select(account => account.ToOptions()).ToArray();
        var result = await _coordinator.RunAsync(selectedAccounts);
        await ApplyResultAsync(result, selectedAccounts);
    }

    [RelayCommand(CanExecute = nameof(CanAuthenticate))]
    private async Task AuthenticateAsync()
    {
        if (CurrentAuthorizationAccount is null)
            return;

        IsAuthorizing = true;
        AuthorizationErrorMessage = string.Empty;
        NotifyStateChanged();
        try
        {
            await _authorizationService.AuthenticateAsync(CurrentAuthorizationAccount.ToOptions());
            await _coordinator.MarkAccountAuthorizationResolvedAsync(
                CurrentAuthorizationAccount.AccountId,
                wasSkipped: false);
            AdvanceAuthorizationQueue();
        }
        catch (Exception ex)
        {
            _logger.CaptureException(ex, "MigrationAccountAuthorization");
            AuthorizationErrorMessage = Wino.Core.Domain.Translator.Migration_AuthorizationFailed;
        }
        finally
        {
            IsAuthorizing = false;
            NotifyStateChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanSkipAuthentication))]
    private async Task SkipAuthenticationAsync()
    {
        if (CurrentAuthorizationAccount is null)
            return;

        IsAuthorizing = true;
        AuthorizationErrorMessage = string.Empty;
        NotifyStateChanged();
        try
        {
            await _authorizationService.SkipAsync(CurrentAuthorizationAccount.ToOptions());
            await _coordinator.MarkAccountAuthorizationResolvedAsync(
                CurrentAuthorizationAccount.AccountId,
                wasSkipped: true);
            AdvanceAuthorizationQueue();
        }
        catch (Exception ex)
        {
            _logger.CaptureException(ex, "MigrationAccountAuthorizationSkip");
            AuthorizationErrorMessage = Wino.Core.Domain.Translator.Migration_AuthorizationSkipFailed;
        }
        finally
        {
            IsAuthorizing = false;
            NotifyStateChanged();
        }
    }

    private bool CanAuthenticate() => CurrentAuthorizationAccount != null && !IsAuthorizing;
    private bool CanSkipAuthentication() => CurrentAuthorizationAccount != null && !IsAuthorizing;

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

    private async Task ApplyResultAsync(
        MigrationResult result,
        IReadOnlyCollection<MigrationAccountOptions> selectedAccounts)
    {
        IsRunning = false;
        _lastResult = result;

        if (result.Status == MigrationStatus.AwaitingUser)
        {
            IsFailed = false;
            IsCompleted = false;
            await BeginAuthorizationAsync(selectedAccounts
                .Where(RequiresFeatureAuthorization)
                .Select(CreateAccountViewModel));
            return;
        }

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

    private async Task BeginAuthorizationAsync(IEnumerable<MigrationAccountOptionViewModel> accounts)
    {
        await _databaseService.InitializeAsync();

        _authorizationQueue.Clear();
        foreach (var account in accounts.Where(account => RequiresFeatureAuthorization(account.ToOptions())))
            _authorizationQueue.Enqueue(account);

        _authorizationAccountCount = _authorizationQueue.Count;
        IsReady = false;
        IsRunning = false;
        IsFailed = false;
        IsCompleted = false;
        Progress = 1;
        AdvanceAuthorizationQueue();
    }

    private void AdvanceAuthorizationQueue()
    {
        AuthorizationErrorMessage = string.Empty;
        CurrentAuthorizationAccount = _authorizationQueue.Count > 0
            ? _authorizationQueue.Dequeue()
            : null;

        if (CurrentAuthorizationAccount != null)
        {
            CurrentTitle = Wino.Core.Domain.Translator.Migration_AuthorizationTitle;
            CurrentDescription = string.Format(
                Wino.Core.Domain.Translator.Migration_AuthorizationDescription,
                CurrentAuthorizationAccount.DisplayName);
        }
        else
        {
            IsCompleted = true;
            CurrentTitle = Wino.Core.Domain.Translator.Migration_SuccessTitle;
            CurrentDescription = Wino.Core.Domain.Translator.Migration_SimpleSuccessDescription;
            var result = _lastResult;
            CompletionSummary = result is null
                ? Wino.Core.Domain.Translator.Migration_AuthorizationComplete
                : $"{result.AccountCount} {Wino.Core.Domain.Translator.Migration_Accounts} · " +
                  $"{result.MailCount} {Wino.Core.Domain.Translator.Migration_Messages} · " +
                  $"{result.CalendarCount} {Wino.Core.Domain.Translator.Migration_CalendarRecords}";
        }

        NotifyStateChanged();
    }

    private static bool RequiresFeatureAuthorization(MigrationAccountOptions account)
        => account.ProviderType is MailProviderType.Gmail or MailProviderType.Outlook &&
           (account.EnableContacts || account.EnableTasks || account.EnableMailFilters);

    private static MigrationAccountOptionViewModel CreateAccountViewModel(MigrationAccountOptions account)
        => new()
        {
            AccountId = account.AccountId,
            DisplayName = account.DisplayName,
            Address = account.Address,
            ProviderType = account.ProviderType,
            EnableContacts = account.EnableContacts,
            EnableTasks = account.EnableTasks,
            EnableMailFilters = account.EnableMailFilters
        };

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
        OnPropertyChanged(nameof(IsAuthorizationVisible));
        OnPropertyChanged(nameof(IsAuthorizationErrorVisible));
        OnPropertyChanged(nameof(CanSkipMigration));
        OnPropertyChanged(nameof(StepProgressText));
        OnPropertyChanged(nameof(AuthorizationProgressText));
    }

    public void Dispose() => _coordinator.ProgressChanged -= OnProgressChanged;
}
