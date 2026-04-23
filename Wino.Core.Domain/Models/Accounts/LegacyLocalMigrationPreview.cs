using System;
using System.Collections.Generic;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Accounts;

public sealed class LegacyLocalMigrationPreview
{
    public string SourceDatabasePath { get; init; } = string.Empty;
    public bool LegacyDatabaseExists { get; init; }
    public bool HasCompletedMigration { get; init; }
    public bool IsPromptDeferred { get; init; }
    public bool ShouldPrompt { get; init; }
    public int LegacyAccountCount { get; init; }
    public int ImportableAccountCount { get; init; }
    public int DuplicateAccountCount { get; init; }
    public int SkippedAccountCount { get; init; }
    public int ImportableMergedInboxCount { get; init; }
    public int SkippedMergedInboxCount { get; init; }
    public IReadOnlyList<LegacyLocalMigrationProviderCount> ProviderCounts { get; init; } = [];
    public IReadOnlyList<LegacyLocalMigrationAccountPreview> Accounts { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public bool HasImportableData => LegacyDatabaseExists && ImportableAccountCount > 0;
}

public sealed class LegacyLocalMigrationProviderCount
{
    public MailProviderType ProviderType { get; init; }
    public int TotalAccountCount { get; init; }
    public int ImportableAccountCount { get; init; }
    public int DuplicateAccountCount { get; init; }
}

public sealed class LegacyLocalMigrationAccountPreview
{
    public Guid LegacyAccountId { get; init; }
    public string Address { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public MailProviderType ProviderType { get; init; }
    public SpecialImapProvider SpecialImapProvider { get; init; }
    public int Order { get; init; }
    public bool CanImport { get; init; }
    public bool IsDuplicate { get; init; }
    public bool IsCalendarEnabled { get; init; }
}
