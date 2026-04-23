using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Mail.ViewModels.Data;

internal static class LegacyLocalMigrationFormatter
{
    public static string BuildPreviewSummary(LegacyLocalMigrationPreview preview)
    {
        if (!preview.HasImportableData)
        {
            return Translator.LegacyLocalMigration_ImportEmpty;
        }

        var providerSummary = string.Join(", ", preview.ProviderCounts
            .Where(a => a.ImportableAccountCount > 0)
            .Select(a => $"{a.ImportableAccountCount} {GetProviderName(a.ProviderType)}"));

        var parts = new List<string>
        {
            string.Format(Translator.LegacyLocalMigration_PreviewSummary, preview.ImportableAccountCount, providerSummary)
        };

        if (preview.DuplicateAccountCount > 0)
        {
            parts.Add(string.Format(Translator.LegacyLocalMigration_PreviewDuplicateSummary, preview.DuplicateAccountCount));
        }

        if (preview.ImportableMergedInboxCount > 0)
        {
            parts.Add(string.Format(Translator.LegacyLocalMigration_PreviewMergedSummary, preview.ImportableMergedInboxCount));
        }

        return string.Join(" ", parts.Where(a => !string.IsNullOrWhiteSpace(a)));
    }

    public static string BuildWarningSummary(LegacyLocalMigrationPreview preview)
        => string.Join(Environment.NewLine, preview.Warnings.Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.Ordinal));

    public static string BuildImportMessage(LegacyLocalMigrationResult result)
    {
        var parts = new List<string>();

        if (result.ImportedAccountCount > 0)
        {
            parts.Add(string.Format(Translator.LegacyLocalMigration_ImportAccountsSucceeded, result.ImportedAccountCount));
        }

        if (result.SkippedDuplicateAccountCount > 0)
        {
            parts.Add(string.Format(Translator.WinoAccount_Management_ImportDuplicateAccountsSkipped, result.SkippedDuplicateAccountCount));
        }

        if (result.ImportedMergedInboxCount > 0)
        {
            parts.Add(string.Format(Translator.LegacyLocalMigration_ImportMergedInboxesSucceeded, result.ImportedMergedInboxCount));
        }

        if (result.SkippedMergedInboxCount > 0)
        {
            parts.Add(string.Format(Translator.LegacyLocalMigration_ImportMergedInboxesSkipped, result.SkippedMergedInboxCount));
        }

        if (result.FailedAccountCount > 0)
        {
            parts.Add(string.Format(Translator.LegacyLocalMigration_ImportFailedAccounts, result.FailedAccountCount));
        }

        if (parts.Count == 0)
        {
            parts.Add(Translator.LegacyLocalMigration_ImportEmpty);
        }

        if (result.ImportedAccountCount > 0)
        {
            parts.Add(Translator.WinoAccount_Management_ImportReloginReminder);
        }

        return string.Join(" ", parts);
    }

    public static string BuildPromptMessage(LegacyLocalMigrationPreview preview)
    {
        var summary = BuildPreviewSummary(preview);
        var warnings = BuildWarningSummary(preview);

        return string.IsNullOrWhiteSpace(warnings)
            ? summary
            : $"{summary}{Environment.NewLine}{Environment.NewLine}{warnings}";
    }

    private static string GetProviderName(MailProviderType providerType)
    {
        return providerType switch
        {
            MailProviderType.Outlook => Translator.LegacyLocalMigration_Provider_Outlook,
            MailProviderType.Gmail => Translator.LegacyLocalMigration_Provider_Gmail,
            MailProviderType.IMAP4 => Translator.LegacyLocalMigration_Provider_Imap,
            _ => providerType.ToString()
        };
    }
}
