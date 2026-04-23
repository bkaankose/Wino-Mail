using System.Collections.Generic;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Accounts;

public sealed class LegacyLocalMigrationResult
{
    public LegacyLocalMigrationPreview Preview { get; init; } = new();
    public int ImportedAccountCount { get; init; }
    public int SkippedDuplicateAccountCount { get; init; }
    public int FailedAccountCount { get; init; }
    public int ImportedMergedInboxCount { get; init; }
    public int SkippedMergedInboxCount { get; init; }
    public IReadOnlyList<LegacyLocalMigrationFailure> Failures { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public bool HasImportedData => ImportedAccountCount > 0 || ImportedMergedInboxCount > 0;
}

public sealed class LegacyLocalMigrationFailure
{
    public string Address { get; init; } = string.Empty;
    public MailProviderType ProviderType { get; init; }
    public string Message { get; init; } = string.Empty;
}
