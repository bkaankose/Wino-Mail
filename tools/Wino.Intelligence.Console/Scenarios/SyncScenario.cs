using System.Diagnostics;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Intelligence.ConsoleApp.Hosting;

namespace Wino.Intelligence.ConsoleApp.Scenarios;

/// <summary>
/// Runs one mail synchronization through the shared SynchronizationManager. The provider's own
/// synchronizer decides between an initial and a delta run from the state it stored.
/// </summary>
internal static class SyncScenario
{
    private static readonly MailSynchronizationType[] Types =
    [
        MailSynchronizationType.FullFolders,
        MailSynchronizationType.InboxOnly,
        MailSynchronizationType.FoldersOnly,
        MailSynchronizationType.ExecuteRequests,
    ];

    public static async Task RunAsync(ScenarioContext context, CancellationToken cancellationToken)
    {
        var typeIndex = ConsoleOutput.Choose("Synchronization type", Types, static type => type switch
        {
            MailSynchronizationType.FullFolders => "FullFolders   every enabled folder (what the app runs every Nth pass)",
            MailSynchronizationType.InboxOnly => "InboxOnly     inbox plus must-have folders (the app's regular pass)",
            MailSynchronizationType.FoldersOnly => "FoldersOnly   folder structure only",
            MailSynchronizationType.ExecuteRequests => "ExecuteRequests  queued local changes only",
            _ => type.ToString(),
        });
        if (typeIndex < 0)
            return;

        await SynchronizeAsync(context, Types[typeIndex], cancellationToken).ConfigureAwait(false);
    }

    public static async Task<MailSynchronizationResult?> SynchronizeAsync(
        ScenarioContext context, MailSynchronizationType type, CancellationToken cancellationToken)
    {
        var account = await context.ReloadAccountAsync().ConfigureAwait(false);
        if (!await EnsureProviderAuthenticationAsync(context, account).ConfigureAwait(false))
            return null;

        var folderService = context.Get<IFolderService>();
        var foldersBefore = await folderService.GetFoldersAsync(account.Id).ConfigureAwait(false);
        var neverSynchronized = foldersBefore.Count(folder => folder.IsSynchronizationEnabled && folder.LastSynchronizedDate is null);
        ConsoleOutput.Muted($"  {foldersBefore.Count} folder(s) known, {neverSynchronized} enabled folder(s) never synchronized.");

        var options = new MailSynchronizationOptions { AccountId = account.Id, Type = type };
        ConsoleOutput.Header($"Synchronizing {account.Address} ({type})");

        var clock = Stopwatch.StartNew();
        var result = await context.Synchronization.SynchronizeAsync(options, cancellationToken).ConfigureAwait(false);
        clock.Stop();

        Print(result, clock.Elapsed);
        return result;
    }

    /// <summary>
    /// Silent token acquisition first, as the synchronizer does. Interactive sign-in only when the
    /// provider says it needs the user, and only after asking.
    /// </summary>
    private static async Task<bool> EnsureProviderAuthenticationAsync(ScenarioContext context, MailAccount account)
    {
        if (account.ProviderType is not (MailProviderType.Outlook or MailProviderType.Gmail))
            return true;

        var authenticator = context.Get<IAuthenticationProvider>().GetAuthenticator(account.ProviderType);
        try
        {
            await ConsoleOutput.TimedAsync($"{account.ProviderType} token",
                () => authenticator.GetTokenInformationAsync(account)).ConfigureAwait(false);
            return true;
        }
        catch (AuthenticationAttentionException)
        {
            if (!ConsoleOutput.Confirm($"{account.ProviderType} needs interactive sign-in for {account.Address}. Sign in now?"))
                return false;

            await authenticator.GenerateTokenInformationAsync(account).ConfigureAwait(false);
            ConsoleOutput.Success("Signed in.");
            return true;
        }
    }

    private static void Print(MailSynchronizationResult result, TimeSpan elapsed)
    {
        var color = result.CompletedState switch
        {
            SynchronizationCompletedState.Success => ConsoleColor.Green,
            SynchronizationCompletedState.PartiallyCompleted => ConsoleColor.Yellow,
            _ => ConsoleColor.Red,
        };

        var line = $"{result.CompletedState} in {ConsoleOutput.Elapsed(elapsed)}: " +
                   $"{result.TotalDownloadedCount} downloaded, {result.TotalUpdatedCount} updated, {result.TotalDeletedCount} deleted " +
                   $"across {result.FolderResults.Count} folder(s).";
        if (color == ConsoleColor.Green) ConsoleOutput.Success(line);
        else if (color == ConsoleColor.Yellow) ConsoleOutput.Warning(line);
        else ConsoleOutput.Error(line);

        foreach (var folder in result.FolderResults.OrderByDescending(static folder => folder.DownloadedCount + folder.UpdatedCount + folder.DeletedCount))
        {
            var text = $"  {(folder.Success ? "ok  " : "FAIL")} {folder.FolderName,-32} +{folder.DownloadedCount,-5} ~{folder.UpdatedCount,-5} -{folder.DeletedCount,-5}" +
                       (string.IsNullOrWhiteSpace(folder.ErrorMessage) ? string.Empty : $" {folder.ErrorCategory}: {folder.ErrorMessage}");
            if (folder.Success) ConsoleOutput.Info(text);
            else ConsoleOutput.Error(text);
        }

        foreach (var issue in result.Issues)
            ConsoleOutput.Warning($"  issue [{issue.Severity}/{issue.Category}] {issue.ScopeName ?? issue.FolderName ?? issue.OperationType}: {issue.Message}");

        if (result.Exception is not null && result.CompletedState != SynchronizationCompletedState.Success)
            ConsoleOutput.Error($"  {result.Exception.GetType().Name}: {result.Exception.Message}");
    }
}
