using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;

namespace Wino.SmokeTest.ConsoleApp;

internal sealed class SmokeTestRunner : IDisposable
{
    private static readonly TimeSpan FixtureDiscoveryTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FixturePollInterval = TimeSpan.FromSeconds(10);
    private static readonly SpecialFolderType[] RequiredFolders =
    [
        SpecialFolderType.Inbox,
        SpecialFolderType.Draft,
        SpecialFolderType.Sent,
        SpecialFolderType.Archive,
        SpecialFolderType.Junk,
        SpecialFolderType.Deleted
    ];

    private readonly IServiceProvider _services;
    private readonly IAccountService _accountService;
    private readonly IFolderService _folderService;
    private readonly IMailService _mailService;
    private readonly IWinoRequestDelegator _requestDelegator;
    private readonly SmokeSynchronizationHost _synchronizationHost;
    private readonly SmokeMailSender _mailSender;

    public SmokeTestRunner(IServiceProvider services)
    {
        _services = services;
        _accountService = services.GetRequiredService<IAccountService>();
        _folderService = services.GetRequiredService<IFolderService>();
        _mailService = services.GetRequiredService<IMailService>();
        _requestDelegator = services.GetRequiredService<IWinoRequestDelegator>();
        _synchronizationHost = new SmokeSynchronizationHost(services.GetRequiredService<ISynchronizationManager>());
        _mailSender = new SmokeMailSender(
            _accountService,
            _folderService,
            _mailService,
            services.GetRequiredService<IMimeFileService>(),
            _requestDelegator,
            _synchronizationHost);
    }

    public async Task<int> RunAutomaticAsync(
        string accountAddress,
        string? reportRecipient,
        CancellationToken cancellationToken)
    {
        var accountMatches = (await _accountService.GetAccountsAsync().ConfigureAwait(false))
            .Where(account => account.IsMailAccessGranted &&
                              string.Equals(account.Address, accountAddress, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (accountMatches.Count != 1)
        {
            ConsoleOutput.Error(accountMatches.Count == 0
                ? $"No mail-enabled account matches '{accountAddress}'."
                : $"More than one mail-enabled account matches '{accountAddress}'.");
            return 2;
        }

        var account = accountMatches[0];
        var startedAt = DateTimeOffset.UtcNow;
        var runId = $"{startedAt:yyyyMMddTHHmmssZ}-{Guid.NewGuid():N}"[..32];
        var result = new SmokeRunResult
        {
            RunId = runId,
            StartedAtUtc = startedAt,
            AccountId = account.Id,
            AccountAddress = account.Address,
            Provider = account.ProviderType.ToString()
        };
        using var artifacts = SmokeRunArtifacts.Create(runId, account.Address);
        _services.GetRequiredService<IWinoLogger>().SetupLogger(artifacts.EngineLogPath);
        ConsoleOutput.Header($"Smoke run {runId}");
        ConsoleOutput.Muted($"Artifacts: {artifacts.FolderPath}");

        var folders = new Dictionary<SpecialFolderType, MailItemFolder>();
        SmokeSentMessage? fixture = null;

        await RunStepAsync(result, "Preflight", async () =>
        {
            if (account.AttentionReason != AccountAttentionReason.None)
            {
                throw new InvalidOperationException(
                    $"The account requires attention ({account.AttentionReason}). Resolve it in Wino before unattended testing.");
            }

            foreach (var folderType in RequiredFolders)
            {
                var folder = await _folderService
                    .GetSpecialFolderByAccountIdAsync(account.Id, folderType)
                    .ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"The {folderType} special folder is not configured.");
                folders[folderType] = folder;
            }

            _ = await _accountService.GetPrimaryAccountAliasAsync(account.Id).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The account has no primary sending alias.");
            return $"Provider {account.ProviderType}; all required special folders and the primary alias are available.";
        }).ConfigureAwait(false);

        await RunMailSynchronizationStepAsync(result, account, cancellationToken).ConfigureAwait(false);
        await RunCalendarSynchronizationStepAsync(result, account, cancellationToken).ConfigureAwait(false);
        await RunContactSynchronizationStepAsync(result, account, cancellationToken).ConfigureAwait(false);

        try
        {
            if (folders.Count == RequiredFolders.Length)
            {
                await RunStepAsync(result, "Send fixture", async () =>
                {
                    fixture = await _mailSender.SendAsync(
                        account,
                        account.Address,
                        $"Wino smoke fixture - {runId}",
                        $"This message belongs to Wino smoke run {runId}.",
                        $"<p>This message belongs to Wino smoke run <b>{runId}</b>.</p>",
                        null,
                        cancellationToken).ConfigureAwait(false);
                    return $"Sent fixture with Message-Id {fixture.MessageId}.";
                }).ConfigureAwait(false);
            }
            else
            {
                AddSkipped(result, "Send fixture", "Preflight did not provide all required folders.");
            }

            MailCopy? fixtureCopy = null;
            if (fixture is not null)
            {
                await RunStepAsync(result, "Discover fixture in Inbox", async () =>
                {
                    fixtureCopy = await WaitForFixtureAsync(
                        account.Id,
                        folders[SpecialFolderType.Inbox],
                        fixture,
                        cancellationToken).ConfigureAwait(false);
                    return $"Found fixture in Inbox as {fixtureCopy.Id}.";
                }).ConfigureAwait(false);
            }
            else
            {
                AddSkipped(result, "Discover fixture in Inbox", "The fixture was not sent.");
            }

            try
            {
                if (fixtureCopy is null || fixture is null)
                {
                    foreach (var name in MutationStepNames)
                        AddSkipped(result, name, "The fixture was not available.");
                }
                else
                {
                    fixtureCopy = await RunStateOperationAsync(result, "Mark read", account, fixtureCopy,
                        MailOperation.MarkAsRead, folders[SpecialFolderType.Inbox], fixture, copy => copy.IsRead,
                        cancellationToken).ConfigureAwait(false);
                    fixtureCopy = await RunStateOperationAsync(result, "Mark unread", account, fixtureCopy,
                        MailOperation.MarkAsUnread, folders[SpecialFolderType.Inbox], fixture, copy => !copy.IsRead,
                        cancellationToken).ConfigureAwait(false);
                    fixtureCopy = await RunStateOperationAsync(result, "Flag", account, fixtureCopy,
                        MailOperation.SetFlag, folders[SpecialFolderType.Inbox], fixture, copy => copy.IsFlagged,
                        cancellationToken).ConfigureAwait(false);
                    fixtureCopy = await RunStateOperationAsync(result, "Unflag", account, fixtureCopy,
                        MailOperation.ClearFlag, folders[SpecialFolderType.Inbox], fixture, copy => !copy.IsFlagged,
                        cancellationToken).ConfigureAwait(false);
                    fixtureCopy = await RunStateOperationAsync(result, "Archive", account, fixtureCopy,
                        MailOperation.Archive, folders[SpecialFolderType.Archive], fixture, _ => true,
                        cancellationToken).ConfigureAwait(false);
                    fixtureCopy = await RunStateOperationAsync(result, "Unarchive", account, fixtureCopy,
                        MailOperation.UnArchive, folders[SpecialFolderType.Inbox], fixture, _ => true,
                        cancellationToken).ConfigureAwait(false);
                    fixtureCopy = await RunStateOperationAsync(result, "Mark junk", account, fixtureCopy,
                        MailOperation.MoveToJunk, folders[SpecialFolderType.Junk], fixture, _ => true,
                        cancellationToken).ConfigureAwait(false);
                    fixtureCopy = await RunStateOperationAsync(result, "Mark not junk", account, fixtureCopy,
                        MailOperation.MarkAsNotJunk, folders[SpecialFolderType.Inbox], fixture, _ => true,
                        cancellationToken).ConfigureAwait(false);
                    fixtureCopy = await RunStateOperationAsync(result, "Soft delete", account, fixtureCopy,
                        MailOperation.SoftDelete, folders[SpecialFolderType.Deleted], fixture, _ => true,
                        cancellationToken).ConfigureAwait(false);

                    await RunStepAsync(result, "Hard delete", async () =>
                    {
                        var inboundUniqueId = fixtureCopy.UniqueId;
                        await ExecuteOperationAsync(account, fixtureCopy, MailOperation.HardDelete, cancellationToken, true)
                            .ConfigureAwait(false);
                        var remainingInboundCopy = await _mailService.GetSingleMailItemAsync(inboundUniqueId).ConfigureAwait(false);
                        if (remainingInboundCopy is not null)
                            throw new InvalidOperationException("The inbound fixture still exists after hard delete.");
                        fixtureCopy = null;
                        return "The inbound fixture copy is absent after hard delete.";
                    }).ConfigureAwait(false);
                }
            }
            finally
            {
                result.FixtureCleanupSucceeded = fixture is null ||
                    await TryCleanupFixtureAsync(account, folders.Values, fixture, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            result.FixtureCleanupSucceeded = fixture is null ||
                await TryCleanupFixtureAsync(account, folders.Values, fixture, CancellationToken.None).ConfigureAwait(false);
            result.FinishedAtUtc = DateTimeOffset.UtcNow;
            artifacts.CompleteEngineLog();
            await artifacts.WriteResultAsync(result, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        var reportText = SmokeReportBuilder.BuildText(result);
        var reportHtml = SmokeReportBuilder.BuildHtml(result);
        try
        {
            await _mailSender.SendAsync(
                account,
                reportRecipient ?? account.Address,
                $"Wino smoke test - {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC",
                reportText,
                reportHtml,
                null,
                cancellationToken).ConfigureAwait(false);
            result.ReportSent = true;
            ConsoleOutput.Success("[Passed] Send report");
        }
        catch (Exception exception)
        {
            result.ReportSendError = exception.Message;
            ConsoleOutput.Error($"[Failed] Send report: {exception.Message}");
        }

        result.FinishedAtUtc = DateTimeOffset.UtcNow;
        artifacts.CompleteEngineLog();
        await artifacts.WriteResultAsync(result, CancellationToken.None).ConfigureAwait(false);
        return result.HasFailures ? 1 : 0;
    }

    public async Task RunInteractiveAsync(
        MailAccount account,
        string attachmentsFolder,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ConsoleOutput.Header("\nSmoke-test actions:");
                System.Console.WriteLine("  1. Synchronize");
                System.Console.WriteLine("  2. Inbox mail operations");
                System.Console.WriteLine("  3. Send basic test mail");
                System.Console.WriteLine("  4. Send Small.data test mail");
                System.Console.WriteLine("  5. Send Large.data test mail");
                System.Console.WriteLine("  0. Back");
                ConsoleOutput.Prompt("Selection: ");

                switch (System.Console.ReadLine()?.Trim())
                {
                    case "1":
                        await RunInteractiveSynchronizationAsync(account, cancellationToken).ConfigureAwait(false);
                        break;
                    case "2":
                        await RunInteractiveMailOperationsAsync(account, cancellationToken).ConfigureAwait(false);
                        break;
                    case "3":
                        await SendManualMailAsync(account, null, cancellationToken).ConfigureAwait(false);
                        break;
                    case "4":
                        await SendManualMailAsync(account, Path.Combine(attachmentsFolder, "Small.data"), cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    case "5":
                        await SendManualMailAsync(account, Path.Combine(attachmentsFolder, "Large.data"), cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    case "0":
                        return;
                    default:
                        ConsoleOutput.Warning("Select a listed action.");
                        break;
                }
            }
        }
        finally
        {
            _synchronizationHost.Dispose();
        }
    }

    private async Task RunInteractiveSynchronizationAsync(MailAccount account, CancellationToken cancellationToken)
    {
        var mail = await _synchronizationHost.SynchronizeMailAsync(new MailSynchronizationOptions
        {
            AccountId = account.Id,
            Type = MailSynchronizationType.FullFolders
        }, cancellationToken).ConfigureAwait(false);
        PrintMailResult(mail);

        if (account.IsCalendarAccessGranted)
        {
            var calendar = await _synchronizationHost.SynchronizeCalendarAsync(new CalendarSynchronizationOptions
            {
                AccountId = account.Id,
                Type = CalendarSynchronizationType.CalendarEvents
            }, cancellationToken).ConfigureAwait(false);
            var changes = _synchronizationHost.LastCalendarChanges;
            var added = Math.Max(changes.Added, calendar.DownloadedEvents?.Count() ?? 0);
            System.Console.WriteLine($"Calendar: {calendar.CompletedState}; added {added}, updated {changes.Updated}, deleted {changes.Deleted}.");
        }
        else
        {
            ConsoleOutput.Warning("Calendar synchronization skipped: access is not granted.");
        }

        if (account.IsContactAccessGranted)
        {
            var contacts = await _synchronizationHost.SynchronizeContactsAsync(new ContactSynchronizationOptions
            {
                AccountId = account.Id,
                Type = ContactSynchronizationType.Delta
            }, cancellationToken).ConfigureAwait(false);
            System.Console.WriteLine($"Contacts: {contacts.CompletedState}; downloaded {contacts.DownloadedCount}, changed {contacts.ChangedCount}, deleted {contacts.DeletedCount}.");
        }
        else
        {
            ConsoleOutput.Warning("Contact synchronization skipped: access is not granted.");
        }
    }

    private async Task RunInteractiveMailOperationsAsync(MailAccount account, CancellationToken cancellationToken)
    {
        var inbox = await _folderService.GetSpecialFolderByAccountIdAsync(account.Id, SpecialFolderType.Inbox)
            .ConfigureAwait(false);
        if (inbox is null)
        {
            ConsoleOutput.Error("Inbox is not configured for this account.");
            return;
        }

        var mails = await _mailService.FetchMailsAsync(new MailListInitializationOptions(
            [inbox], FilterOptionType.All, SortingOptionType.ReceiveDate, false, null, string.Empty, Take: 10),
            cancellationToken).ConfigureAwait(false);
        if (mails.Count == 0)
        {
            ConsoleOutput.Warning("Inbox is empty.");
            return;
        }

        for (var index = 0; index < mails.Count; index++)
        {
            var mail = mails[index];
            System.Console.WriteLine($"  {index + 1}. {mail.CreationDate:g} | {mail.FromAddress} | {mail.Subject} | read={mail.IsRead} flag={mail.IsFlagged}");
        }

        ConsoleOutput.Prompt("Select a message, or 0 to cancel: ");
        if (!int.TryParse(System.Console.ReadLine(), out var selection) || selection < 1 || selection > mails.Count)
            return;

        MailCopy? selected = mails[selection - 1];
        while (selected is not null && !cancellationToken.IsCancellationRequested)
        {
            System.Console.WriteLine($"\nSelected: {selected.Subject} | folder={selected.AssignedFolder?.FolderName} | read={selected.IsRead} | flag={selected.IsFlagged}");
            System.Console.WriteLine("  1 Read  2 Unread  3 Flag  4 Unflag  5 Archive  6 Unarchive");
            System.Console.WriteLine("  7 Junk  8 Not junk  9 Soft delete  10 Hard delete  0 Back");
            ConsoleOutput.Prompt("Operation: ");
            var operationInput = System.Console.ReadLine();
            if (operationInput?.Trim() == "0")
                return;
            if (!TryMapManualOperation(operationInput, out var operation))
            {
                ConsoleOutput.Warning("Select a listed operation.");
                continue;
            }

            if (operation == MailOperation.HardDelete)
            {
                ConsoleOutput.Prompt($"Type DELETE to permanently delete '{selected.Subject}': ");
                if (!string.Equals(System.Console.ReadLine(), "DELETE", StringComparison.Ordinal))
                {
                    ConsoleOutput.Warning("Hard delete cancelled.");
                    continue;
                }
            }

            try
            {
                var selectedUniqueId = selected.UniqueId;
                await ExecuteOperationAsync(account, selected, operation, cancellationToken, operation == MailOperation.HardDelete)
                    .ConfigureAwait(false);
                ConsoleOutput.Success($"{operation} completed through the production request path.");
                selected = await _mailService.GetSingleMailItemAsync(selectedUniqueId).ConfigureAwait(false);
                if (selected is null)
                    ConsoleOutput.Warning("The selected message no longer exists locally.");
            }
            catch (Exception exception)
            {
                ConsoleOutput.Error($"{operation} failed: {exception.Message}");
            }
        }
    }

    private async Task SendManualMailAsync(MailAccount account, string? attachmentPath, CancellationToken cancellationToken)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            await _mailSender.SendAsync(
                account,
                account.Address,
                $"Wino smoke test - {now:yyyy-MM-dd HH:mm:ss} UTC",
                "This is a manually triggered Wino smoke-test message.",
                "<p>This is a manually triggered Wino smoke-test message.</p>",
                attachmentPath,
                cancellationToken).ConfigureAwait(false);
            ConsoleOutput.Success("Test message sent.");
        }
        catch (Exception exception)
        {
            ConsoleOutput.Error($"Test message failed: {exception.Message}");
        }
    }

    private async Task RunMailSynchronizationStepAsync(
        SmokeRunResult result,
        MailAccount account,
        CancellationToken cancellationToken)
    {
        await RunStepAsync(result, "Mail synchronization", async () =>
        {
            var sync = await _synchronizationHost.SynchronizeMailAsync(new MailSynchronizationOptions
            {
                AccountId = account.Id,
                Type = MailSynchronizationType.FullFolders
            }, cancellationToken).ConfigureAwait(false);
            EnsureSuccessful(sync.CompletedState, sync.Exception, sync.AllIssues.Select(issue => issue.Message));
            return new StepPayload(
                $"{sync.SuccessfulFolderCount} folders succeeded; {sync.FailedFolderCount} failed.",
                new Dictionary<string, int>
                {
                    ["arrived"] = sync.TotalDownloadedCount,
                    ["updated"] = sync.TotalUpdatedCount,
                    ["deleted"] = sync.TotalDeletedCount
                });
        }).ConfigureAwait(false);
    }

    private async Task RunCalendarSynchronizationStepAsync(
        SmokeRunResult result,
        MailAccount account,
        CancellationToken cancellationToken)
    {
        if (!account.IsCalendarAccessGranted)
        {
            AddSkipped(result, "Calendar synchronization", "Calendar access is not granted.");
            return;
        }

        await RunStepAsync(result, "Calendar synchronization", async () =>
        {
            var sync = await _synchronizationHost.SynchronizeCalendarAsync(new CalendarSynchronizationOptions
            {
                AccountId = account.Id,
                Type = CalendarSynchronizationType.CalendarEvents
            }, cancellationToken).ConfigureAwait(false);
            EnsureSuccessful(sync.CompletedState, sync.Exception, sync.AllIssues.Select(issue => issue.Message));
            var changes = _synchronizationHost.LastCalendarChanges;
            var added = Math.Max(changes.Added, sync.DownloadedEvents?.Count() ?? 0);
            return new StepPayload("Calendar event synchronization completed.", new Dictionary<string, int>
            {
                ["added"] = added,
                ["updated"] = changes.Updated,
                ["deleted"] = changes.Deleted
            });
        }).ConfigureAwait(false);
    }

    private async Task RunContactSynchronizationStepAsync(
        SmokeRunResult result,
        MailAccount account,
        CancellationToken cancellationToken)
    {
        if (!account.IsContactAccessGranted)
        {
            AddSkipped(result, "Contact synchronization", "Contact access is not granted.");
            return;
        }

        await RunStepAsync(result, "Contact synchronization", async () =>
        {
            var sync = await _synchronizationHost.SynchronizeContactsAsync(new ContactSynchronizationOptions
            {
                AccountId = account.Id,
                Type = ContactSynchronizationType.Delta
            }, cancellationToken).ConfigureAwait(false);
            EnsureSuccessful(sync.CompletedState, sync.Exception, sync.Issues.Select(issue => issue.Message));
            return new StepPayload("Contact synchronization completed.", new Dictionary<string, int>
            {
                ["downloaded"] = sync.DownloadedCount,
                ["changed"] = sync.ChangedCount,
                ["deleted"] = sync.DeletedCount
            });
        }).ConfigureAwait(false);
    }

    private async Task<MailCopy?> RunStateOperationAsync(
        SmokeRunResult result,
        string name,
        MailAccount account,
        MailCopy? current,
        MailOperation operation,
        MailItemFolder expectedFolder,
        SmokeSentMessage fixture,
        Func<MailCopy, bool> statePredicate,
        CancellationToken cancellationToken)
    {
        if (current is null)
        {
            AddSkipped(result, name, "A preceding fixture operation failed.");
            return null;
        }

        MailCopy? refreshed = null;
        await RunStepAsync(result, name, async () =>
        {
            await ExecuteOperationAsync(account, current, operation, cancellationToken).ConfigureAwait(false);
            refreshed = await FindFixtureInFolderAsync(expectedFolder, fixture).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Fixture was not found in {expectedFolder.FolderName}.");
            if (!statePredicate(refreshed))
                throw new InvalidOperationException("The expected message state was not persisted.");
            return $"Verified in {expectedFolder.FolderName}.";
        }).ConfigureAwait(false);
        return refreshed;
    }

    private async Task ExecuteOperationAsync(
        MailAccount account,
        MailCopy mail,
        MailOperation operation,
        CancellationToken cancellationToken,
        bool ignoreHardDeleteProtection = false)
    {
        var sync = await _synchronizationHost.ExecuteMailOperationAsync(
            account.Id,
            () => _requestDelegator.ExecuteAsync(new MailOperationPreperationRequest(
                operation,
                mail,
                ignoreHardDeleteProtection: ignoreHardDeleteProtection)),
            cancellationToken).ConfigureAwait(false);
        EnsureSuccessful(sync.CompletedState, sync.Exception, sync.AllIssues.Select(issue => issue.Message));
    }

    private async Task<MailCopy> WaitForFixtureAsync(
        Guid accountId,
        MailItemFolder inbox,
        SmokeSentMessage fixture,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + FixtureDiscoveryTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var found = await FindFixtureInFolderAsync(inbox, fixture).ConfigureAwait(false);
            if (found is not null)
                return found;

            await _synchronizationHost.SynchronizeMailAsync(new MailSynchronizationOptions
            {
                AccountId = accountId,
                Type = MailSynchronizationType.InboxOnly
            }, cancellationToken).ConfigureAwait(false);
            await Task.Delay(FixturePollInterval, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("The self-addressed fixture did not arrive in Inbox within five minutes.");
    }

    private async Task<MailCopy?> FindFixtureInFolderAsync(MailItemFolder folder, SmokeSentMessage fixture)
        => (await _mailService.GetMailsByFolderIdAsync(folder.Id).ConfigureAwait(false))
            .FirstOrDefault(mail => IsFixture(mail, fixture));

    private async Task<List<MailCopy>> FindFixtureCopiesAsync(
        Guid accountId,
        IEnumerable<MailItemFolder> folders,
        SmokeSentMessage fixture)
    {
        var matches = new List<MailCopy>();
        foreach (var folder in folders.DistinctBy(folder => folder.Id))
        {
            matches.AddRange((await _mailService.GetMailsByFolderIdAsync(folder.Id).ConfigureAwait(false))
                .Where(mail => mail.AssignedAccount?.Id == accountId && IsFixture(mail, fixture)));
        }
        return matches.DistinctBy(mail => mail.UniqueId).ToList();
    }

    private async Task<bool> TryCleanupFixtureAsync(
        MailAccount account,
        IEnumerable<MailItemFolder> folders,
        SmokeSentMessage fixture,
        CancellationToken cancellationToken)
    {
        try
        {
            var matches = await FindFixtureCopiesAsync(account.Id, folders, fixture).ConfigureAwait(false);
            foreach (var match in matches)
                await ExecuteOperationAsync(account, match, MailOperation.HardDelete, cancellationToken, true).ConfigureAwait(false);
            return (await FindFixtureCopiesAsync(account.Id, folders, fixture).ConfigureAwait(false)).Count == 0;
        }
        catch (Exception exception)
        {
            ConsoleOutput.Error($"Fixture cleanup failed: {exception.Message}");
            return false;
        }
    }

    private static bool IsFixture(MailCopy mail, SmokeSentMessage fixture)
        => string.Equals(mail.MessageId, fixture.MessageId, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(mail.Subject, fixture.Subject, StringComparison.Ordinal);

    private static async Task RunStepAsync(
        SmokeRunResult result,
        string name,
        Func<Task<string>> action)
        => await RunStepAsync(result, name, async () => new StepPayload(await action().ConfigureAwait(false), null))
            .ConfigureAwait(false);

    private static async Task RunStepAsync(
        SmokeRunResult result,
        string name,
        Func<Task<StepPayload>> action)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var payload = await action().ConfigureAwait(false);
            result.Steps.Add(new SmokeStepResult(name, SmokeStepStatus.Passed, stopwatch.Elapsed, payload.Details, payload.Counts));
            ConsoleOutput.Success($"[Passed] {name}: {payload.Details}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            result.Steps.Add(new SmokeStepResult(name, SmokeStepStatus.Failed, stopwatch.Elapsed, exception.Message));
            ConsoleOutput.Error($"[Failed] {name}: {exception.Message}");
        }
    }

    private static void AddSkipped(SmokeRunResult result, string name, string details)
    {
        result.Steps.Add(new SmokeStepResult(name, SmokeStepStatus.Skipped, TimeSpan.Zero, details));
        ConsoleOutput.Warning($"[Skipped] {name}: {details}");
    }

    private static void EnsureSuccessful(
        SynchronizationCompletedState state,
        Exception? exception,
        IEnumerable<string?> issues)
    {
        if (state == SynchronizationCompletedState.Success)
            return;

        var issueText = string.Join(" | ", issues.Where(issue => !string.IsNullOrWhiteSpace(issue)));
        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(issueText)
                ? $"Synchronization completed with state {state}."
                : $"Synchronization completed with state {state}: {issueText}",
            exception);
    }

    private static void PrintMailResult(MailSynchronizationResult result)
    {
        System.Console.WriteLine($"Mail: {result.CompletedState}; arrived {result.TotalDownloadedCount}, updated {result.TotalUpdatedCount}, deleted {result.TotalDeletedCount}.");
        foreach (var folder in result.FolderResults)
            System.Console.WriteLine($"  {folder.FolderName}: success={folder.Success}, arrived={folder.DownloadedCount}, updated={folder.UpdatedCount}, deleted={folder.DeletedCount}");
        foreach (var issue in result.AllIssues)
            ConsoleOutput.Warning($"  {issue.Message}");
    }

    internal static bool TryMapManualOperation(string? input, out MailOperation operation)
    {
        operation = input?.Trim() switch
        {
            "1" => MailOperation.MarkAsRead,
            "2" => MailOperation.MarkAsUnread,
            "3" => MailOperation.SetFlag,
            "4" => MailOperation.ClearFlag,
            "5" => MailOperation.Archive,
            "6" => MailOperation.UnArchive,
            "7" => MailOperation.MoveToJunk,
            "8" => MailOperation.MarkAsNotJunk,
            "9" => MailOperation.SoftDelete,
            "10" => MailOperation.HardDelete,
            _ => MailOperation.None
        };
        return operation != MailOperation.None;
    }

    private static readonly string[] MutationStepNames =
    [
        "Mark read", "Mark unread", "Flag", "Unflag", "Archive", "Unarchive",
        "Mark junk", "Mark not junk", "Soft delete", "Hard delete"
    ];

    private readonly record struct StepPayload(string Details, IReadOnlyDictionary<string, int>? Counts);

    public void Dispose() => _synchronizationHost.Dispose();
}
