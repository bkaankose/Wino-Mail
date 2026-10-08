using System;
using System.Collections.Generic;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Migration;

public enum MigrationStatus
{
    NotRequired,
    Required,
    Running,
    AwaitingUser,
    Failed,
    Completed,
    Skipped
}

public enum MigrationStepStatus
{
    Pending,
    Running,
    AwaitingUser,
    Completed,
    Failed
}

public enum MigrationStepKind
{
    CheckExistingData = 1,
    ChooseFeatures = 2,
    PrepareDatabase = 3,
    MigrateAccountsAndSettings = 4,
    MigrateMailAndFiles = 5,
    MigrateCalendars = 6,
    ReconnectAccounts = 7,
    ConfigureFeatures = 8,
    ValidateAndFinalize = 9,
    Completed = 10
}

public sealed record MigrationAccountOptions(
    Guid AccountId,
    string DisplayName,
    string Address,
    MailProviderType ProviderType,
    bool EnableContacts,
    bool EnableTasks,
    bool EnableMailFilters,
    bool DeferSignIn = true);

public sealed record MigrationPlan(
    MigrationStatus Status,
    string SourcePath,
    string StagingPath,
    string DestinationPath,
    bool CanResume,
    IReadOnlyList<MigrationAccountOptions> Accounts,
    string Message = null);

public sealed record MigrationProgress(
    MigrationStepKind Step,
    MigrationStepStatus Status,
    string Title,
    string Description,
    double Progress,
    string Detail = null);

public sealed record MigrationResult(
    MigrationStatus Status,
    MigrationStepKind? FailedStep = null,
    string ErrorMessage = null,
    int AccountCount = 0,
    long MailCount = 0,
    long CalendarCount = 0,
    IReadOnlyDictionary<string, long> MigratedRowCounts = null);
