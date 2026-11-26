#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Reader;
using Wino.Services.Interfaces;

namespace Wino.Mail.ViewModels;

public partial class ComposePageViewModel
{
    private readonly IMailProviderRegistry? _providerRegistry;

    /// <summary>
    /// Looks the draft over for the problems the user chose in Settings and asks what to do about them. True sends,
    /// false keeps the composer open. A fix or a description is applied and the draft is checked again.
    /// </summary>
    private async Task<bool> ConfirmDraftChecksAsync()
    {
        if (DraftChecks is not { } checks || !PreferencesService.IsDraftCheckEnabled)
            return true;

        var imageDescriptions = PreferencesService.IsDraftImageDescriptionCheckEnabled;
        var readableText = PreferencesService.IsDraftReadableTextCheckEnabled;
        var maximumBytes = PreferencesService.IsDraftMessageSizeCheckEnabled ? GetAccountMessageLimit() : 0;

        if (!imageDescriptions && !readableText && maximumBytes <= 0)
            return true;

        try
        {
            while (true)
            {
                var issues = await checks.CheckAsync(imageDescriptions, readableText, maximumBytes);
                if (issues.Count == 0)
                    return true;

                var result = await _dialogService.ShowDraftCheckDialogAsync(issues.Select(issue => ToDialogItem(issue, maximumBytes)).ToList());

                switch (result.Choice)
                {
                    case DraftCheckDialogChoice.SendAnyway:
                        return true;
                    case DraftCheckDialogChoice.IssueAction when result.IssueIndex >= 0 && result.IssueIndex < issues.Count:
                        var issue = issues[result.IssueIndex];

                        switch (result.Action)
                        {
                            case DraftCheckIssueAction.Show:
                                await checks.SelectAsync(issue);
                                return false;
                            case DraftCheckIssueAction.Fix:
                                if (await checks.FixAsync(issue))
                                    _dialogService.InfoBarMessage(Translator.DraftCheckDialog_Title, Translator.DraftCheck_TextFixed, InfoBarMessageType.Information);
                                break;
                            case DraftCheckIssueAction.Describe:
                                await checks.DescribeAsync(issue);
                                break;
                            case DraftCheckIssueAction.NotNeeded:
                                await checks.MarkDecorativeAsync(issue);
                                break;
                        }

                        break;
                    default:
                        return false;
                }
            }
        }
        catch (Exception ex)
        {
            // A check that fails must not keep the user from sending.
            Log.Warning(ex, "The checks before sending failed; the message is sent without them.");
            return true;
        }
    }

    private long GetAccountMessageLimit()
    {
        var account = CurrentMailDraftItem?.MailCopy?.AssignedAccount ?? ComposingAccount;

        return account is not null && _providerRegistry?.GetTraits(account.ProviderType).MaximumMessageBytes is long limit ? limit : 0;
    }

    private static DraftCheckIssueItem ToDialogItem(ComposeDraftIssue issue, long maximumBytes) => issue.Kind switch
    {
        ComposeDraftCheckKind.ImageDescription => new DraftCheckIssueItem(
            Translator.DraftCheck_ImageTitle, Translator.DraftCheck_ImageDetail,
            CanShow: false, CanFix: false, CanDescribe: true, CanMarkNotNeeded: true),
        ComposeDraftCheckKind.ReadableText => new DraftCheckIssueItem(
            Translator.DraftCheck_ReadableTitle,
            string.Format(CultureInfo.CurrentCulture, Translator.DraftCheck_ReadableDetailFormat, issue.Measured.ToString("0.0", CultureInfo.CurrentCulture)),
            CanShow: true, CanFix: issue.CanFix, CanDescribe: false, CanMarkNotNeeded: false),
        _ => new DraftCheckIssueItem(
            string.Format(CultureInfo.CurrentCulture, Translator.DraftCheck_SizeTitleFormat, FormatBytes(issue.Measured)),
            string.Format(CultureInfo.CurrentCulture, Translator.DraftCheck_SizeDetailFormat, FormatBytes(maximumBytes)),
            CanShow: false, CanFix: false, CanDescribe: false, CanMarkNotNeeded: false)
    };

    private static string FormatBytes(double bytes)
        => bytes >= 1024 * 1024
            ? $"{(bytes / (1024 * 1024)).ToString("0.0", CultureInfo.CurrentCulture)} MB"
            : $"{Math.Max(1, Math.Round(bytes / 1024)).ToString(CultureInfo.CurrentCulture)} KB";
}
