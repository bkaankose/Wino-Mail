using System.Globalization;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wino.Core;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.Domain.Models.SemanticIndexing;
using Wino.Core.Services;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.Contracts.Intelligence;
using Wino.Services;

namespace Wino.SmokeTest.ConsoleApp;

internal static class Program
{
    private const string LocalApiUrl = "https://localhost:7204/";
    private const string ProductionApiUrl = "https://api.winomail.app/";
    private const string PublisherRelativePath = @"Publishers\mhdqskaa8n2sj\WinoShared";
    private const string DebugLocalStateRelativePath = @"Packages\58272BurakKSE.WinoMailPreview.Debug_mhdqskaa8n2sj\LocalState";
    private const string PreviewLocalStateRelativePath = @"Packages\58272BurakKSE.WinoMailPreview_mhdqskaa8n2sj\LocalState";
    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        if (!TryParseArguments(args, out var options, out var error))
        {
            ConsoleOutput.Error(error);
            PrintHelp();
            return 2;
        }

        if (options.ShowHelp)
        {
            PrintHelp();
            return 0;
        }

        var isInteractive = options.Stress is null &&
                            options.Smoke is null &&
                            options.DailyBriefingAddress is null &&
                            options.IndexAccountAddress is null &&
                            options.TodoGroupProbeAddress is null;
        var apiEnvironment = options.Stress?.Environment ?? ApiEnvironment.Local;
        var apiUri = GetApiUri(apiEnvironment);
        var stressRunId = options.Stress is null ? null : $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}-{Guid.NewGuid():N}"[..32];
        var paths = ResolvePaths(options);
        if (!ValidatePaths(paths))
            return 2;

        if (!SmokeProcessGuard.TryAcquire(out var processGuard, out var guardError))
        {
            ConsoleOutput.Error(guardError);
            return 2;
        }

        using var activeProcessGuard = processGuard;

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
            ConsoleOutput.Warning("\nCancellation requested. Waiting for active work to stop...");
        };
        System.Console.CancelKeyPress += cancelHandler;

        try
        {
            if (!isInteractive)
                ConsoleOutput.Header($"\nAPI: {apiEnvironment} ({apiUri})");
            System.Console.WriteLine($"Database folder: {paths.PublisherFolder}");
            System.Console.WriteLine($"Application data: {paths.ApplicationDataFolder}");
            ConsoleOutput.Success("Wino Mail is closed and the smoke-console database lock is active.\n");

            await using var services = CreateServices(
                paths,
                apiUri,
                apiEnvironment,
                options.Smoke is null,
                options.Stress,
                stressRunId);
            await InitializeServicesAsync(services, !isInteractive && options.Smoke is null, cancellation.Token)
                .ConfigureAwait(false);
            if (options.Smoke is not null)
            {
                using var smokeRunner = new SmokeTestRunner(services);
                return await smokeRunner
                    .RunAutomaticAsync(options.Smoke.AccountAddress, options.Smoke.ReportRecipient, cancellation.Token)
                    .ConfigureAwait(false);
            }
            if (options.Stress is not null)
            {
                return await new StressRunner(options.Stress, services, apiUri, stressRunId!)
                    .RunAsync(cancellation.Token).ConfigureAwait(false);
            }
            if (options.DailyBriefingAddress is not null)
            {
                return await RunDailyBriefingAsync(services, options.DailyBriefingAddress,
                    cancellation.Token).ConfigureAwait(false);
            }
            if (options.TodoGroupProbeAddress is not null)
            {
                return await SubstrateTaskGroupProbe.RunAsync(
                    options.TodoGroupProbeAddress,
                    paths.ApplicationDataFolder,
                    cancellation.Token).ConfigureAwait(false);
            }
            if (options.IndexAccountAddress is not null)
            {
                return await RunIndexDiagnosticAsync(
                    services,
                    options.IndexAccountAddress,
                    options.IndexFolderName ?? "Inbox",
                    options.IndexMessageCount,
                    options.ResetIntelligence,
                    cancellation.Token).ConfigureAwait(false);
            }

            return await RunAsync(
                services,
                options.AttachmentsFolder ?? Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "Attachments")),
                cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            ConsoleOutput.Warning("Operation cancelled.");
            return 130;
        }
        catch (Exception exception)
        {
            ConsoleOutput.Error($"Fatal error: {exception.Message}");
            return 1;
        }
        finally
        {
            System.Console.CancelKeyPress -= cancelHandler;
        }
    }

    internal static Uri GetApiUri(ApiEnvironment environment)
        => new(environment == ApiEnvironment.Production ? ProductionApiUrl : LocalApiUrl);

    internal static bool ShouldBypassCertificate(ApiEnvironment environment, Uri requestUri)
        => environment == ApiEnvironment.Local && requestUri.IsLoopback;

    internal static bool IsSupportedAccount(MailAccount account)
        => account.ProviderType is MailProviderType.Outlook or MailProviderType.Gmail or MailProviderType.IMAP4;

    internal static bool HasIntelligence(SemanticIndexAccountState state)
        => state.ServerHead?.StorageSizeBytes > 0 ||
           state.ServerHead?.OldestAnalyzedMessageUtc is not null ||
           state.LastImportedVersion > 0;

    internal static bool HasIncompleteIntelligence(SemanticIndexAccountState state)
        => state.WaitingMessageCount > 0;

    internal static SemanticIndexRangePreset? ParseRangeSelection(string? selection)
        => selection?.Trim() switch
        {
            "" or null or "1" => SemanticIndexRangePreset.OneWeek,
            "2" => SemanticIndexRangePreset.OneMonth,
            "3" => SemanticIndexRangePreset.OneYear,
            _ => null,
        };

    private static ServiceProvider CreateServices(
        ConsolePaths paths,
        Uri apiUri,
        ApiEnvironment environment,
        bool allowExternalLaunch,
        StressOptions? stressOptions = null, string? stressRunId = null)
    {
        var nativeAppService = new ConsoleNativeAppService(paths.ApplicationDataFolder, allowExternalLaunch);
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        serviceCollection.RegisterCoreServices();
        serviceCollection.RegisterSharedServices();
        serviceCollection.AddSingleton<IConfigurationService, ConsoleConfigurationService>();
        serviceCollection.AddSingleton(ConsolePreferencesProxy.Create());
        serviceCollection.AddSingleton<INativeAppService>(nativeAppService);
        serviceCollection.AddSingleton<IAppMetadataService>(nativeAppService);
        serviceCollection.AddSingleton<INotificationBuilder, ConsoleNotificationBuilder>();
        serviceCollection.AddSingleton<IKeyPressService, ConsoleKeyPressService>();
        serviceCollection.AddSingleton(ConsoleDefaultProxy<IMailDialogService>.Create());
        serviceCollection.AddSingleton(ConsoleDefaultProxy<IStatePersistanceService>.Create());
        serviceCollection.AddSingleton<IAuthenticatorConfig, MailAuthenticatorConfiguration>();
        serviceCollection.AddSingleton(provider =>
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.Brotli | DecompressionMethods.GZip | DecompressionMethods.Deflate,
                ServerCertificateCustomValidationCallback = (request, _, _, errors) =>
                    request.RequestUri is not null && ShouldBypassCertificate(environment, request.RequestUri) ||
                    errors == System.Net.Security.SslPolicyErrors.None,
            };
            HttpMessageHandler transport = handler;
            if (stressOptions is not null)
                transport = new StressMeasurementHandler(handler, stressRunId!);
            return new HttpClient(transport)
            {
                BaseAddress = apiUri,
                Timeout = TimeSpan.FromMinutes(10),
            };
        });
        serviceCollection.AddSingleton<IWinoAccountApiClient>(provider =>
        {
            return new WinoAccountApiClient(
                provider.GetRequiredService<IDatabaseService>(),
                provider.GetRequiredService<HttpClient>(),
                provider.GetRequiredService<Wino.Mail.AI.Abstractions.IContentEnvelopeEncryptor>(),
                provider.GetRequiredService<ITranslationService>(),
                maximumEncryptedAttempts: stressOptions is null ? 5 : 1);
        });

        var provider = serviceCollection.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = false,
        });
        var configuration = provider.GetRequiredService<IApplicationConfiguration>();
        configuration.PublisherSharedFolderPath = paths.PublisherFolder;
        configuration.ApplicationDataFolderPath = paths.ApplicationDataFolder;
        configuration.ApplicationTempFolderPath = paths.TempFolder;
        return provider;
    }

    private static async Task InitializeServicesAsync(
        IServiceProvider services,
        bool initializeIntelligence,
        CancellationToken cancellationToken)
    {
        await services.GetRequiredService<IDatabaseService>().InitializeAsync().ConfigureAwait(false);
        await services.GetRequiredService<ITranslationService>().InitializeAsync().ConfigureAwait(false);
        await services.GetRequiredService<SynchronizationManagerInitializer>().InitializeAsync().ConfigureAwait(false);
        if (initializeIntelligence)
            await InitializeIntelligenceServicesAsync(services).ConfigureAwait(false);
    }

    private static async Task InitializeIntelligenceServicesAsync(IServiceProvider services)
    {
        await services.GetRequiredService<ILocalIntelligenceStore>().InitializeAsync().ConfigureAwait(false);
        await services.GetRequiredService<ISemanticIndexCoordinator>().InitializeAsync().ConfigureAwait(false);
    }

    private static async Task<int> RunAsync(
        IServiceProvider services,
        string attachmentsFolder,
        CancellationToken cancellationToken)
    {
        var accountService = services.GetRequiredService<IAccountService>();
        var coordinator = services.GetRequiredService<ISemanticIndexCoordinator>();
        var localStore = services.GetRequiredService<ILocalIntelligenceStore>();
        var apiClient = services.GetRequiredService<IWinoAccountApiClient>();
        var authenticationProvider = services.GetRequiredService<IAuthenticationProvider>();
        var messageResolver = services.GetRequiredService<IIntelligenceMessageContextResolver>();
        var mailService = services.GetRequiredService<IMailService>();
        var httpClient = services.GetRequiredService<HttpClient>();
        ApiEnvironment? selectedApiEnvironment = null;
        var intelligenceInitialized = false;

        async Task EnsureIntelligenceReadyAsync(CancellationToken token)
        {
            if (selectedApiEnvironment is null)
            {
                selectedApiEnvironment = SelectApiEnvironment();
                httpClient.BaseAddress = GetApiUri(selectedApiEnvironment.Value);
                ConsoleOutput.Header($"\nAPI: {selectedApiEnvironment} ({httpClient.BaseAddress})");
            }

            if (intelligenceInitialized)
                return;

            token.ThrowIfCancellationRequested();
            await InitializeIntelligenceServicesAsync(services).ConfigureAwait(false);
            intelligenceInitialized = true;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            var accounts = await accountService.GetAccountsAsync().ConfigureAwait(false);
            if (accounts.Count == 0)
            {
                ConsoleOutput.Warning("No mail accounts were found in Wino210.db.");
                return 1;
            }

            var selected = SelectAccount(accounts);
            if (selected is null)
                return 0;

            try
            {
                await RunAccountAsync(selected, accountService, coordinator, apiClient, authenticationProvider,
                        messageResolver, localStore, mailService, services, attachmentsFolder,
                        EnsureIntelligenceReadyAsync, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                ConsoleOutput.Error($"Account operation failed: {exception.Message}");
            }
        }

        return 0;
    }

    private static async Task<int> RunDailyBriefingAsync(
        IServiceProvider services, string address, CancellationToken cancellationToken)
    {
        var accountService = services.GetRequiredService<IAccountService>();
        var localService = services.GetRequiredService<ILocalIntelligenceService>();
        var accounts = await accountService.GetAccountsAsync().ConfigureAwait(false);
        var account = accounts.FirstOrDefault(item =>
            string.Equals(item.Address.Trim(), address.Trim(), StringComparison.OrdinalIgnoreCase));

        if (account is null)
        {
            ConsoleOutput.Error($"Account was not found in Wino210.db: {address}");
            return 1;
        }

        var eligibleAccounts = await localService.GetEligibleAccountsAsync(cancellationToken).ConfigureAwait(false);
        var eligible = eligibleAccounts.FirstOrDefault(item => item.Account.Id == account.Id);
        var access = await localService.GetAccessSnapshotAsync(account.Id, cancellationToken).ConfigureAwait(false);
        var timeZone = TimeZoneInfo.Local;
        var today = GetLocalToday(timeZone);
        ConsoleOutput.Header($"\nDaily briefing diagnostic: {account.Address}");
        System.Console.WriteLine($"  Account id: {account.Id:D}");
        System.Console.WriteLine($"  Provider: {account.ProviderType}");
        System.Console.WriteLine($"  Mail access granted: {account.IsMailAccessGranted}");
        System.Console.WriteLine($"  Access snapshot: {(access is null ? "missing" : "present")}");
        if (access is not null)
        {
            System.Console.WriteLine($"  Wino account id: {access.WinoAccountId:D}");
            System.Console.WriteLine($"  AI pack: {access.HasAiPack}");
            System.Console.WriteLine($"  Intelligence consent: {access.HasIntelligenceConsent}");
            System.Console.WriteLine($"  Mailbox id: {access.MailboxId?.ToString("D") ?? "none"}");
        }
        System.Console.WriteLine($"  Eligible for briefing: {eligible is not null}");

        if (eligible is null)
        {
            ConsoleOutput.Warning("The account is filtered out before briefing facts are queried.");
            var storedTodayCount = await CountStoredBriefingFactsAsync(account.Id, today,
                timeZone, services, cancellationToken).ConfigureAwait(false);
            var storedYesterdayCount = await CountStoredBriefingFactsAsync(account.Id,
                today.AddDays(-1), timeZone, services, cancellationToken).ConfigureAwait(false);
            System.Console.WriteLine($"  Stored local facts for today: {storedTodayCount}");
            System.Console.WriteLine($"  Stored local facts for yesterday: {storedYesterdayCount}");
            return 1;
        }

        var yesterday = today.AddDays(-1);
        var todayFacts = await localService.GetBriefingFactsAsync(today, timeZone, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var yesterdayFacts = await localService.GetBriefingFactsAsync(yesterday, timeZone, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var todayCount = todayFacts.Facts.Count(item => item.LocalAccountId == account.Id);
        var yesterdayCount = yesterdayFacts.Facts.Count(item => item.LocalAccountId == account.Id);

        System.Console.WriteLine($"  {today:yyyy-MM-dd}: {todayCount} briefing fact(s)");
        System.Console.WriteLine($"  {yesterday:yyyy-MM-dd}: {yesterdayCount} briefing fact(s)");
        if (todayCount >= 2 && yesterdayCount >= 2)
        {
            ConsoleOutput.Success("Daily briefing has at least two insights for both days.");
            return 0;
        }

        ConsoleOutput.Warning("Expected briefing facts are missing for one or both days.");
        return 1;
    }

    private static async Task<int> RunIndexDiagnosticAsync(
        IServiceProvider services,
        string address,
        string folderName,
        int messageCount,
        bool resetIntelligence,
        CancellationToken cancellationToken)
    {
        var accountService = services.GetRequiredService<IAccountService>();
        var coordinator = services.GetRequiredService<ISemanticIndexCoordinator>();
        var localStore = services.GetRequiredService<ILocalIntelligenceStore>();
        var apiClient = services.GetRequiredService<IWinoAccountApiClient>();
        var authenticationProvider = services.GetRequiredService<IAuthenticationProvider>();
        var messageResolver = services.GetRequiredService<IIntelligenceMessageContextResolver>();
        var folderService = services.GetRequiredService<IFolderService>();
        var accounts = await accountService.GetAccountsAsync().ConfigureAwait(false);
        var account = accounts.FirstOrDefault(item =>
            string.Equals(item.Address.Trim(), address.Trim(), StringComparison.OrdinalIgnoreCase));
        if (account is null)
        {
            ConsoleOutput.Error($"Account was not found in Wino210.db: {address}");
            return 1;
        }

        if (!IsSupportedAccount(account))
        {
            ConsoleOutput.Error($"The diagnostic does not support {account.ProviderType} accounts: {address}");
            return 1;
        }

        if (resetIntelligence)
        {
            ConsoleOutput.Warning("Deleting all local and server intelligence for this account...");
            await coordinator.DeleteIndexAsync(account.Id, cancellationToken).ConfigureAwait(false);
            account.Preferences.IsSemanticIndexingEnabled = true;
            await accountService.UpdateAccountAsync(account).ConfigureAwait(false);
            ConsoleOutput.Success("All intelligence data was deleted. Intelligence was enabled for the test run.");
        }

        var folders = await folderService.GetFoldersAsync(account.Id).ConfigureAwait(false);
        var folder = folders.FirstOrDefault(item =>
            string.Equals(item.FolderName, folderName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.RemoteFolderId, folderName, StringComparison.OrdinalIgnoreCase));
        if (folder is null)
        {
            ConsoleOutput.Error($"Folder was not found: {folderName}");
            return 1;
        }

        if (!await EnsureIntelligenceEnabledAsync(
                account, accountService, coordinator, apiClient, cancellationToken).ConfigureAwait(false) ||
            !await EnsureProviderAuthenticationAsync(account, authenticationProvider).ConfigureAwait(false))
        {
            return 1;
        }

        var selectionTimer = System.Diagnostics.Stopwatch.StartNew();
        var candidates = await messageResolver.GetCandidatesAsync(
            account.Id, cancellationToken: cancellationToken).ConfigureAwait(false);
        var remoteMessageIds = candidates
            .Where(candidate => candidate.RemoteFolderIds.Contains(folder.RemoteFolderId, StringComparer.Ordinal))
            .OrderByDescending(static candidate => candidate.ReceivedAt)
            .Select(static candidate => candidate.RemoteMessageId)
            .Distinct(StringComparer.Ordinal)
            .Take(messageCount)
            .ToArray();
        selectionTimer.Stop();

        ConsoleOutput.Header($"\nIndex diagnostic: {account.Address}");
        System.Console.WriteLine($"  Folder: {folder.FolderName} ({folder.RemoteFolderId})");
        System.Console.WriteLine($"  Selected: {remoteMessageIds.Length:N0}");
        System.Console.WriteLine($"  Selection elapsed: {selectionTimer.Elapsed.TotalSeconds:0.00}s");
        if (remoteMessageIds.Length == 0)
            return 1;

        await ProbeMessageContentAsync(account, candidates, remoteMessageIds, messageResolver, cancellationToken)
            .ConfigureAwait(false);

        var totalTimer = System.Diagnostics.Stopwatch.StartNew();
        await coordinator.StartIndexingAsync(account.Id, remoteMessageIds, cancellationToken).ConfigureAwait(false);
        var completed = await MonitorJobAsync(account.Id, coordinator, cancellationToken).ConfigureAwait(false);
        totalTimer.Stop();
        if (!completed)
            return 1;

        var snapshot = coordinator.GetJobSnapshot(account.Id);
        System.Console.WriteLine($"  Total elapsed: {totalTimer.Elapsed.TotalSeconds:0.00}s");
        System.Console.WriteLine($"  Restored: {snapshot.RestoredMessageCount:N0}");
        System.Console.WriteLine($"  Newly indexed: {snapshot.UploadedMessageCount:N0}");
        System.Console.WriteLine($"  Failed: {snapshot.FailedMessageCount:N0}");
        if (snapshot.UploadedMessageCount > 0)
            System.Console.WriteLine($"  New-message throughput: {snapshot.UploadedMessageCount / totalTimer.Elapsed.TotalSeconds:0.00} messages/s");
        var state = await coordinator.GetStateAsync(account.Id, cancellationToken).ConfigureAwait(false);
        var documents = await localStore.GetCurrentDocumentsAsync(
            account.Id,
            remoteMessageIds,
            cancellationToken).ConfigureAwait(false);
        var invalidDocuments = documents.Values.Count(static document =>
            string.IsNullOrWhiteSpace(document.Analysis.SourceLanguage) ||
            string.IsNullOrWhiteSpace(document.Analysis.Headline) ||
            string.IsNullOrWhiteSpace(document.Analysis.Summary) ||
            document.Analysis.Confidence is < 0 or > 1 ||
            document.EmbeddingDimensions != 768 ||
            !string.Equals(document.EmbeddingEncoding, "float32-le", StringComparison.Ordinal) ||
            Convert.FromBase64String(document.Embedding).Length != 3_072);
        var stateMatches = state?.ServerHead is { } head &&
            state.LocalState is { } localState &&
            string.Equals(head.IntelligenceVersion, WinoIntelligenceVersions.V1, StringComparison.Ordinal) &&
            string.Equals(localState.IntelligenceVersion, head.IntelligenceVersion, StringComparison.Ordinal) &&
            localState.IndexEpoch == head.IndexEpoch &&
            localState.LastImportedRevision == head.ArtifactRevision;
        var accountedFor = snapshot.SucceededMessageCount + snapshot.FailedMessageCount;

        System.Console.WriteLine($"  Accounted for: {accountedFor:N0}/{remoteMessageIds.Length:N0}");
        System.Console.WriteLine($"  Synchronized documents: {documents.Count:N0}/{remoteMessageIds.Length:N0}");
        System.Console.WriteLine($"  Invalid documents: {invalidDocuments:N0}");
        System.Console.WriteLine($"  Version/epoch synchronized: {stateMatches}");

        if (snapshot.Status != SemanticIndexJobStatus.Completed ||
            snapshot.FailedMessageCount != 0 ||
            accountedFor != remoteMessageIds.Length ||
            documents.Count != remoteMessageIds.Length ||
            invalidDocuments != 0 ||
            !stateMatches)
        {
            ConsoleOutput.Error("The V1 indexing diagnostic did not meet all pass criteria.");
            return 1;
        }

        ConsoleOutput.Success("The V1 indexing diagnostic passed all document, embedding, and cursor checks.");
        return 0;
    }

    private static async Task ProbeMessageContentAsync(
        MailAccount account,
        IReadOnlyList<IntelligenceMessageCandidate> candidates,
        IReadOnlyCollection<string> selectedRemoteMessageIds,
        IIntelligenceMessageContextResolver messageResolver,
        CancellationToken cancellationToken)
    {
        var successfulCount = 0;
        foreach (var candidate in candidates.Where(candidate =>
                     selectedRemoteMessageIds.Contains(candidate.RemoteMessageId, StringComparer.Ordinal)).Take(10))
        {
            try
            {
                var content = await messageResolver.GetContentAsync(account.Id, candidate, cancellationToken).ConfigureAwait(false);
                successfulCount++;
                ConsoleOutput.Muted(
                    $"Content probe {successfulCount:N0} succeeded for {account.ProviderType}: " +
                    $"{content.Body.Format}, {content.Body.Content.Length:N0} characters.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                ConsoleOutput.Error(
                    $"Content probe failed for {account.ProviderType} message {candidate.ProviderMessageId}: " +
                    $"{exception.GetType().Name}: {exception.Message}");
                return;
            }
        }

        ConsoleOutput.Success($"Content probe succeeded for {successfulCount:N0} {account.ProviderType} message(s).");
    }

    private static async Task<int> CountStoredBriefingFactsAsync(
        Guid accountId, DateOnly date, TimeZoneInfo timeZone, IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var intelligence = services.GetRequiredService<ILocalIntelligenceService>();
        var facts = await intelligence.GetBriefingFactsAsync(date, timeZone, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return facts.Facts.Count(fact => fact.LocalAccountId == accountId);
    }

    private static DateOnly GetLocalToday(TimeZoneInfo timeZone)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone).DateTime);

    private static async Task RunAccountAsync(
        MailAccount account,
        IAccountService accountService,
        ISemanticIndexCoordinator coordinator,
        IWinoAccountApiClient apiClient,
        IAuthenticationProvider authenticationProvider,
        IIntelligenceMessageContextResolver messageResolver,
        ILocalIntelligenceStore localStore,
        IMailService mailService,
        IServiceProvider services,
        string attachmentsFolder,
        Func<CancellationToken, Task> ensureIntelligenceReadyAsync,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            account = await accountService.GetAccountAsync(account.Id).ConfigureAwait(false);
            if (account is null)
                return;

            System.Console.WriteLine($"\n{FormatAccountSelection(account)}");
            ConsoleOutput.Header("\nAccount actions:");
            System.Console.WriteLine("  1. Synchronize");
            System.Console.WriteLine("  2. Smoke tests");
            System.Console.WriteLine("  3. Semantic search");
            System.Console.WriteLine("  4. Manage intelligence indexing");
            System.Console.WriteLine("  5. Invalidate authentication data");
            System.Console.WriteLine("  0. Back");
            ConsoleOutput.Prompt("Selection: ");
            switch (System.Console.ReadLine()?.Trim())
            {
                case "1":
                    using (var synchronizationRunner = new SmokeTestRunner(services))
                    {
                        await synchronizationRunner.RunSynchronizationMenuAsync(account, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    break;
                case "2":
                    using (var smokeRunner = new SmokeTestRunner(services))
                    {
                        await smokeRunner.RunInteractiveAsync(account, attachmentsFolder, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    break;
                case "3" when IsSupportedAccount(account):
                    await ensureIntelligenceReadyAsync(cancellationToken).ConfigureAwait(false);
                    var state = await coordinator.GetStateAsync(account.Id, cancellationToken).ConfigureAwait(false);
                    PrintState(state);
                    await RunSemanticSearchMenuAsync(
                        account, state, apiClient, messageResolver, mailService, cancellationToken).ConfigureAwait(false);
                    break;
                case "4" when IsSupportedAccount(account):
                    await ensureIntelligenceReadyAsync(cancellationToken).ConfigureAwait(false);
                    await RunIntelligenceManagementMenuAsync(
                        account, accountService, coordinator, apiClient, authenticationProvider,
                        messageResolver, localStore, cancellationToken).ConfigureAwait(false);
                    break;
                case "3" or "4":
                    ConsoleOutput.Warning($"Intelligence tools do not support {account.ProviderType} accounts.");
                    break;
                case "5":
                    if (await InvalidateAuthenticationDataAsync(
                            account,
                            accountService,
                            services.GetRequiredService<ISynchronizationManager>(),
                            cancellationToken).ConfigureAwait(false))
                    {
                        return;
                    }
                    break;
                case "0":
                    return;
                default:
                    ConsoleOutput.Warning("Select a listed action.");
                    break;
            }
        }
    }

    private static async Task RunIntelligenceManagementMenuAsync(
        MailAccount account,
        IAccountService accountService,
        ISemanticIndexCoordinator coordinator,
        IWinoAccountApiClient apiClient,
        IAuthenticationProvider authenticationProvider,
        IIntelligenceMessageContextResolver messageResolver,
        ILocalIntelligenceStore localStore,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var state = await coordinator.GetStateAsync(account.Id, cancellationToken).ConfigureAwait(false);
            await PrintIntelligenceDashboardAsync(
                account, state, coordinator, apiClient, messageResolver, localStore, cancellationToken).ConfigureAwait(false);

            var snapshot = coordinator.GetJobSnapshot(account.Id);
            ConsoleOutput.Header("\nIntelligence indexing actions:");
            System.Console.WriteLine($"  1. {(state.IsEnabled ? "Disable and delete intelligence" : "Enable intelligence")}");
            System.Console.WriteLine("  2. Choose date range and start indexing");
            System.Console.WriteLine("  3. Monitor indexing job");
            System.Console.WriteLine("  4. Cancel indexing job");
            System.Console.WriteLine("  5. Download and apply available cloud intelligence");
            System.Console.WriteLine("  6. Start a new intelligence epoch and index");
            System.Console.WriteLine("  7. Translate briefing headlines");
            System.Console.WriteLine("  8. Delete local intelligence cache only");
            System.Console.WriteLine("  9. Refresh status and coverage chart");
            System.Console.WriteLine(" 10. Verify downloaded metadata for a date range");
            System.Console.WriteLine("  0. Back");
            ConsoleOutput.Prompt("Selection: ");

            switch (System.Console.ReadLine()?.Trim())
            {
                case "1":
                    if (state.IsEnabled)
                        await DisableAndDeleteIntelligenceAsync(account, accountService, coordinator, cancellationToken)
                            .ConfigureAwait(false);
                    else
                        await EnableIntelligenceAsync(account, accountService, coordinator, apiClient, cancellationToken)
                            .ConfigureAwait(false);
                    break;
                case "2":
                    {
                        if (!await EnsureIntelligenceEnabledAsync(account, accountService, coordinator, apiClient, cancellationToken)
                                .ConfigureAwait(false))
                            break;

                        var range = await SelectIndexingRangeAsync(
                            account, coordinator, messageResolver, localStore, cancellationToken).ConfigureAwait(false);
                        if (range is null)
                            break;

                        await StartIndexingAsync(
                            account, accountService, coordinator, apiClient, messageResolver, authenticationProvider, range, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    }
                case "3":
                    {
                        if (!snapshot.IsActive)
                            ConsoleOutput.Warning("There is no active indexing job.");
                        else
                            await MonitorJobAsync(account.Id, coordinator, cancellationToken).ConfigureAwait(false);
                        break;
                    }
                case "4":
                    if (!snapshot.IsActive)
                        ConsoleOutput.Warning("There is no active indexing job to cancel.");
                    else if (Confirm("Cancel the active indexing job?"))
                    {
                        await coordinator.CancelIndexingAsync(account.Id).ConfigureAwait(false);
                        ConsoleOutput.Success("Indexing cancellation requested.");
                    }
                    break;
                case "5":
                    if (!state.CanDownload)
                        ConsoleOutput.Warning("No cloud intelligence is available to download for this account.");
                    else
                        await DownloadAvailableIntelligenceAsync(
                            account, coordinator, messageResolver, localStore, cancellationToken).ConfigureAwait(false);
                    break;
                case "6":
                    if (!await EnsureIntelligenceEnabledAsync(account, accountService, coordinator, apiClient, cancellationToken)
                            .ConfigureAwait(false))
                        break;

                    var mailboxId = await GetMailboxIdAsync(account, state, apiClient, cancellationToken).ConfigureAwait(false);
                    if (mailboxId is null)
                    {
                        ConsoleOutput.Warning("This account has no semantic mailbox.");
                        break;
                    }

                    if (!Confirm("Start a new intelligence epoch for this mailbox?"))
                        break;

                    var manifest = await apiClient.GetWinoIntelligenceManifestAsync(cancellationToken).ConfigureAwait(false);
                    await apiClient.BeginIntelligenceReindexAsync(
                        mailboxId.Value,
                        new BeginIntelligenceReindexRequest(manifest.LatestIntelligenceVersion, Guid.NewGuid()),
                        cancellationToken).ConfigureAwait(false);
                    ConsoleOutput.Success("New intelligence epoch started.");
                    var rebuildRange = await SelectIndexingRangeAsync(
                        account, coordinator, messageResolver, localStore, cancellationToken).ConfigureAwait(false);
                    if (rebuildRange is not null)
                        await StartIndexingAsync(
                            account, accountService, coordinator, apiClient, messageResolver, authenticationProvider, rebuildRange, cancellationToken)
                            .ConfigureAwait(false);
                    break;
                case "7":
                    if (!state.IsEnabled)
                    {
                        ConsoleOutput.Warning("Enable intelligence before translating briefing headlines.");
                        break;
                    }

                    var defaultLanguage = string.IsNullOrWhiteSpace(CultureInfo.CurrentUICulture.Name)
                        ? "en-US"
                        : CultureInfo.CurrentUICulture.Name;
                    ConsoleOutput.Prompt($"Target language [{defaultLanguage}]: ");
                    var language = System.Console.ReadLine()?.Trim();
                    language = string.IsNullOrWhiteSpace(language) ? defaultLanguage : language;
                    var translated = await coordinator.TranslateHeadlinesAsync(account.Id, language, cancellationToken)
                        .ConfigureAwait(false);
                    ConsoleOutput.Success($"Briefing headlines translated to {translated.HeadlineLanguage}.");
                    break;
                case "8":
                    if (Confirm("Delete only the local intelligence cache? Cloud intelligence will remain available."))
                    {
                        await coordinator.DeleteLocalIndexAsync(account.Id, cancellationToken).ConfigureAwait(false);
                        ConsoleOutput.Success("Local intelligence cache deleted.");
                    }
                    break;
                case "9":
                    break;
                case "10":
                    {
                        var verificationRange = await SelectIndexingRangeAsync(
                            account, coordinator, messageResolver, localStore, cancellationToken).ConfigureAwait(false);
                        if (verificationRange is null)
                            break;

                        await VerifyLocalMetadataAsync(
                            account.Id,
                            verificationRange.CutoffUtc,
                            verificationRange.ThroughUtcExclusive,
                            messageResolver,
                            localStore,
                            cancellationToken).ConfigureAwait(false);
                        break;
                    }
                case "0":
                    return;
                default:
                    ConsoleOutput.Warning("Select a listed action.");
                    break;
            }
        }
    }

    private static async Task EnableIntelligenceAsync(
        MailAccount account,
        IAccountService accountService,
        ISemanticIndexCoordinator coordinator,
        IWinoAccountApiClient apiClient,
        CancellationToken cancellationToken)
    {
        if (!await EnsureProcessConsentAsync(account, apiClient, cancellationToken).ConfigureAwait(false))
            return;

        await coordinator.EnsureMailboxAsync(account.Id, cancellationToken).ConfigureAwait(false);
        account.Preferences.IsSemanticIndexingEnabled = true;
        await accountService.UpdateAccountAsync(account).ConfigureAwait(false);
        ConsoleOutput.Success("Intelligence enabled.");

        var state = await coordinator.GetStateAsync(account.Id, cancellationToken).ConfigureAwait(false);
        if (state.CanDownload)
            await DownloadAvailableIntelligenceAsync(account, coordinator, null, null, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> EnsureIntelligenceEnabledAsync(
        MailAccount account,
        IAccountService accountService,
        ISemanticIndexCoordinator coordinator,
        IWinoAccountApiClient apiClient,
        CancellationToken cancellationToken)
    {
        if (account.Preferences.IsSemanticIndexingEnabled)
            return true;

        if (!Confirm("Intelligence is disabled. Enable it now?"))
            return false;

        await EnableIntelligenceAsync(account, accountService, coordinator, apiClient, cancellationToken).ConfigureAwait(false);
        return account.Preferences.IsSemanticIndexingEnabled;
    }

    private static async Task DisableAndDeleteIntelligenceAsync(
        MailAccount account,
        IAccountService accountService,
        ISemanticIndexCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        if (!Confirm("Delete this account's server and local intelligence?"))
            return;

        await coordinator.DeleteIndexAsync(account.Id, cancellationToken).ConfigureAwait(false);
        account.Preferences.IsSemanticIndexingEnabled = false;
        await accountService.UpdateAccountAsync(account).ConfigureAwait(false);
        ConsoleOutput.Success("Intelligence disabled and deleted. Process consent was preserved.");
    }

    private static async Task DownloadAvailableIntelligenceAsync(
        MailAccount account,
        ISemanticIndexCoordinator coordinator,
        IIntelligenceMessageContextResolver? messageResolver,
        ILocalIntelligenceStore? localStore,
        CancellationToken cancellationToken)
    {
        ConsoleOutput.Header("Downloading available intelligence metadata...");
        var progress = new Progress<SemanticIndexingProgress>(value =>
            System.Console.WriteLine($"  Metadata: {value.CompletedMessageCount}/{value.TotalMessageCount}"));
        var result = await coordinator.DownloadAvailableIntelligenceAsync(account.Id, progress, cancellationToken)
            .ConfigureAwait(false);
        var state = await coordinator.GetStateAsync(account.Id, cancellationToken).ConfigureAwait(false);
        ConsoleOutput.Success($"Downloaded intelligence for {result.CoveredRemoteMessageIds.Count:N0} local message(s).");
        PrintState(state);

        if (messageResolver is not null && localStore is not null)
            await PrintCoverageChartAsync(account, state, messageResolver, localStore, null, cancellationToken)
                .ConfigureAwait(false);
    }

    private static async Task StartIndexingAsync(
        MailAccount account,
        IAccountService accountService,
        ISemanticIndexCoordinator coordinator,
        IWinoAccountApiClient apiClient,
        IIntelligenceMessageContextResolver messageResolver,
        IAuthenticationProvider authenticationProvider,
        ConsoleIndexingRange range,
        CancellationToken cancellationToken)
    {
        var snapshot = coordinator.GetJobSnapshot(account.Id);
        if (snapshot.IsActive)
        {
            ConsoleOutput.Warning("An indexing job is already active. Monitor or cancel it before starting another one.");
            return;
        }

        ConsoleOutput.Muted("Resolving selected messages...");
        var candidates = await messageResolver.GetCandidatesAsync(
            account.Id,
            range.CutoffUtc,
            range.ThroughUtcExclusive,
            cancellationToken).ConfigureAwait(false);
        var remoteMessageIds = candidates
            .Select(static candidate => candidate.RemoteMessageId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (remoteMessageIds.Length == 0)
        {
            ConsoleOutput.Success("The selected date range contains no messages.");
            return;
        }

        if (!Confirm($"Reconcile and index {remoteMessageIds.Length} selected messages?"))
            return;

        if (!await EnsureProviderAuthenticationAsync(account, authenticationProvider).ConfigureAwait(false))
            return;

        await coordinator.StartIndexingAsync(account.Id, remoteMessageIds, cancellationToken).ConfigureAwait(false);
        ConsoleOutput.Success("Indexing started. Use the monitor or cancel action to follow the job.");
    }

    private static async Task<ConsoleIndexingRange?> SelectIndexingRangeAsync(
        MailAccount account,
        ISemanticIndexCoordinator coordinator,
        IIntelligenceMessageContextResolver messageResolver,
        ILocalIntelligenceStore localStore,
        CancellationToken cancellationToken)
    {
        var availableRange = await messageResolver.GetAvailableRangeAsync(account.Id, cancellationToken).ConfigureAwait(false);
        if (availableRange is null)
        {
            ConsoleOutput.Warning("No locally available messages can be indexed for this account.");
            return null;
        }

        ConsoleOutput.Header("\nIndexing date range:");
        System.Console.WriteLine($"  Available: {availableRange.OldestDate:dd.MM.yyyy} – {availableRange.NewestDate:dd.MM.yyyy}");
        System.Console.WriteLine("  1. Only new messages");
        System.Console.WriteLine("  2. One week");
        System.Console.WriteLine("  3. One month");
        System.Console.WriteLine("  4. Three months");
        System.Console.WriteLine("  5. Six months");
        System.Console.WriteLine("  6. One year");
        System.Console.WriteLine("  7. Everything");
        System.Console.WriteLine("  8. Custom dates (dd.MM.yyyy)");
        System.Console.WriteLine("  0. Back");
        ConsoleOutput.Prompt("Selection [3]: ");

        var selection = System.Console.ReadLine()?.Trim();
        if (selection == "0")
            return null;

        DateOnly start;
        DateOnly end;
        var preset = selection switch
        {
            "1" => SemanticIndexRangePreset.OnlyNew,
            "2" => SemanticIndexRangePreset.OneWeek,
            "4" => SemanticIndexRangePreset.ThreeMonths,
            "5" => SemanticIndexRangePreset.SixMonths,
            "6" => SemanticIndexRangePreset.OneYear,
            "7" => SemanticIndexRangePreset.Everything,
            "8" => SemanticIndexRangePreset.Custom,
            "" or null or "3" => SemanticIndexRangePreset.OneMonth,
            _ => (SemanticIndexRangePreset?)null,
        };
        if (preset is null)
        {
            ConsoleOutput.Warning("Select a listed indexing range.");
            return null;
        }

        if (preset.Value == SemanticIndexRangePreset.Custom)
        {
            if (!TryReadCustomDateRange(availableRange, out start, out end))
                return null;
        }
        else
        {
            var days = SemanticIndexRangeSelectionResolver.GetPresetDays(preset.Value, availableRange);
            start = availableRange.OldestDate.AddDays(Math.Max(0, availableRange.DaySpan - days));
            end = availableRange.NewestDate;
        }

        await PrintCoverageChartAsync(
            account,
            await coordinator.GetStateAsync(account.Id, cancellationToken).ConfigureAwait(false),
            messageResolver,
            localStore,
            new ConsoleDateRange(start, end),
            cancellationToken).ConfigureAwait(false);

        ConsoleOutput.Prompt("Automatically index new messages [Y/n]: ");
        var automaticallyIndexNewMessages = !string.Equals(System.Console.ReadLine()?.Trim(), "n", StringComparison.OrdinalIgnoreCase);
        return new ConsoleIndexingRange(
            preset.Value,
            ToStartOfLocalDayUtc(start),
            ToStartOfLocalDayUtc(end.AddDays(1)),
            automaticallyIndexNewMessages,
            start,
            end);
    }

    internal static bool TryParseConsoleDate(string? value, out DateOnly date)
        => DateOnly.TryParseExact(value, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static bool TryReadCustomDateRange(
        SemanticIndexAvailableRange availableRange,
        out DateOnly start,
        out DateOnly end)
    {
        start = default;
        end = default;
        ConsoleOutput.Prompt("Start date (dd.MM.yyyy, or 0 to cancel): ");
        var startText = System.Console.ReadLine()?.Trim();
        if (startText == "0")
            return false;
        if (!TryParseConsoleDate(startText, out start))
        {
            ConsoleOutput.Warning("Use a valid date in exact dd.MM.yyyy format, for example 09.02.2026.");
            return false;
        }

        ConsoleOutput.Prompt("End date (dd.MM.yyyy, inclusive): ");
        if (!TryParseConsoleDate(System.Console.ReadLine()?.Trim(), out end))
        {
            ConsoleOutput.Warning("Use a valid date in exact dd.MM.yyyy format, for example 28.02.2026.");
            return false;
        }

        if (start > end)
        {
            ConsoleOutput.Warning("The start date must be on or before the end date.");
            return false;
        }

        if (start < availableRange.OldestDate || end > availableRange.NewestDate)
        {
            ConsoleOutput.Warning(
                $"Custom dates must stay within {availableRange.OldestDate:dd.MM.yyyy} – {availableRange.NewestDate:dd.MM.yyyy}.");
            return false;
        }

        return true;
    }

    private static DateTimeOffset ToStartOfLocalDayUtc(DateOnly date)
        => new DateTimeOffset(DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Local)).ToUniversalTime();

    private static async Task<Guid?> GetMailboxIdAsync(
        MailAccount account,
        SemanticIndexAccountState state,
        IWinoAccountApiClient apiClient,
        CancellationToken cancellationToken)
    {
        if (state.ServerMailboxId is { } mailboxId)
            return mailboxId;

        var mailboxes = await apiClient.GetSemanticMailboxesAsync(cancellationToken).ConfigureAwait(false);
        return mailboxes.SingleOrDefault(mailbox =>
            mailbox.ProviderType == (int)account.ProviderType &&
            string.Equals(mailbox.Address.Trim(), account.Address.Trim(), StringComparison.OrdinalIgnoreCase))?.MailboxId;
    }

    private static async Task PrintIntelligenceDashboardAsync(
        MailAccount account,
        SemanticIndexAccountState state,
        ISemanticIndexCoordinator coordinator,
        IWinoAccountApiClient apiClient,
        IIntelligenceMessageContextResolver messageResolver,
        ILocalIntelligenceStore localStore,
        CancellationToken cancellationToken)
    {
        PrintState(state);
        var mailboxId = await GetMailboxIdAsync(account, state, apiClient, cancellationToken).ConfigureAwait(false);
        if (mailboxId is not null)
        {
            try
            {
                var head = await apiClient.GetIntelligenceHeadAsync(mailboxId.Value, cancellationToken).ConfigureAwait(false);
                System.Console.WriteLine($"  Intelligence version: {head?.IntelligenceVersion ?? "not initialized"}");
                System.Console.WriteLine($"  Index epoch: {head?.IndexEpoch.ToString() ?? "none"}");
                System.Console.WriteLine($"  Indexed messages: {head?.IndexedMessageCount ?? 0}");
            }
            catch (Exception exception)
            {
                ConsoleOutput.Warning($"Could not refresh the remote intelligence head: {exception.Message}");
            }
        }

        try
        {
            var usage = await apiClient.GetAiUsageAsync(cancellationToken).ConfigureAwait(false);
            if (usage.IsSuccess && usage.Result is not null)
                System.Console.WriteLine($"  AI quota: {usage.Result.UsagePercentage}% used; resets {usage.Result.ResetsAtUtc?.LocalDateTime.ToString("dd.MM.yyyy HH:mm") ?? "unknown"}");
        }
        catch (Exception exception)
        {
            ConsoleOutput.Warning($"Could not refresh AI quota: {exception.Message}");
        }

        await PrintCoverageChartAsync(account, state, messageResolver, localStore, null, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task PrintCoverageChartAsync(
        MailAccount account,
        SemanticIndexAccountState state,
        IIntelligenceMessageContextResolver messageResolver,
        ILocalIntelligenceStore localStore,
        ConsoleDateRange? selection,
        CancellationToken cancellationToken)
    {
        var availableRange = await messageResolver.GetAvailableRangeAsync(account.Id, cancellationToken).ConfigureAwait(false);
        var localOldest = availableRange?.OldestDate;
        var localNewest = availableRange?.NewestDate;
        var cloudOldest = state.ServerHead?.OldestAnalyzedMessageUtc is { } oldest
            ? DateOnly.FromDateTime(oldest.UtcDateTime)
            : (DateOnly?)null;
        var cloudNewest = state.ServerHead?.NewestAnalyzedMessageUtc is { } newest
            ? DateOnly.FromDateTime(newest.UtcDateTime)
            : (DateOnly?)null;
        var oldestDate = MinDate(localOldest, cloudOldest);
        var newestDate = MaxDate(localNewest, cloudNewest);
        if (oldestDate is null || newestDate is null)
        {
            ConsoleOutput.Muted("Coverage chart: no local or cloud messages are available.");
            return;
        }

        var candidates = await messageResolver.GetCandidatesAsync(account.Id, cancellationToken: cancellationToken).ConfigureAwait(false);
        var documents = await localStore.GetCurrentDocumentsAsync(account.Id, cancellationToken).ConfigureAwait(false);
        var candidateKeys = candidates
            .Select(static candidate => candidate.RemoteMessageId)
            .ToHashSet(StringComparer.Ordinal);
        var documentKeys = documents
            .Select(static document => document.ServerMessageKey)
            .ToHashSet(StringComparer.Ordinal);
        var totalDays = newestDate.Value.DayNumber - oldestDate.Value.DayNumber + 1;
        var bucketCount = Math.Min(72, totalDays);
        var buckets = Enumerable.Range(0, bucketCount).Select(index =>
        {
            var startDayOffset = index * totalDays / bucketCount;
            var endDayOffset = (index + 1) * totalDays / bucketCount;
            var bucketStart = DateOnly.FromDayNumber(oldestDate.Value.DayNumber + startDayOffset);
            var bucketEndExclusive = DateOnly.FromDayNumber(oldestDate.Value.DayNumber + endDayOffset);
            var startUtc = ToStartOfUtcDay(bucketStart);
            var endUtc = ToStartOfUtcDay(bucketEndExclusive);
            var local = candidates
                .Where(candidate => candidate.ReceivedAt >= startUtc && candidate.ReceivedAt < endUtc)
                .ToArray();
            var localAndCloud = local.Count(candidate => documentKeys.Contains(candidate.RemoteMessageId));
            var localOnly = local.Length - localAndCloud;
            var cloudOnly = documents.Count(document =>
                document.ReceivedAtUtc >= startUtc &&
                document.ReceivedAtUtc < endUtc &&
                !candidateKeys.Contains(document.ServerMessageKey));
            return new ConsoleCoverageBucket(
                bucketStart,
                bucketEndExclusive.AddDays(-1),
                localAndCloud,
                localOnly,
                cloudOnly);
        }).ToArray();
        PrintCoverageChart(buckets, selection);
    }

    private static void PrintCoverageChart(IReadOnlyList<ConsoleCoverageBucket> buckets, ConsoleDateRange? selection)
    {
        ConsoleOutput.Header("\nCoverage chart:");
        System.Console.WriteLine("  Legend: █ local + cloud   ▓ local only   ░ cloud only   · no messages");
        if (selection is not null)
            System.Console.WriteLine($"  Selected: {selection.Value.Start:dd.MM.yyyy} – {selection.Value.End:dd.MM.yyyy}");
        if (buckets.Count == 0)
        {
            ConsoleOutput.Muted("  No coverage buckets were returned.");
            return;
        }

        var maximum = Math.Max(1L, buckets.Max(bucket => bucket.Total));
        const int chartHeight = 8;
        for (var row = chartHeight; row >= 1; row--)
        {
            System.Console.Write("  ");
            foreach (var bucket in buckets)
            {
                var height = (int)Math.Ceiling(bucket.Total * chartHeight / (double)maximum);
                var character = height < row ? ' ' : bucket.Character;
                WriteCoverageCharacter(character, selection is not null && bucket.Overlaps(selection.Value));
            }
            System.Console.WriteLine();
        }

        System.Console.WriteLine($"  {buckets[0].Start:dd.MM.yyyy}{new string(' ', Math.Max(1, buckets.Count - 22))}{buckets[^1].End:dd.MM.yyyy}");
        System.Console.WriteLine($"  Local + cloud: {buckets.Sum(bucket => bucket.LocalAndCloud):N0} | Local only: {buckets.Sum(bucket => bucket.LocalOnly):N0} | Cloud only: {buckets.Sum(bucket => bucket.CloudOnly):N0}");
    }

    private static void WriteCoverageCharacter(char value, bool isSelected)
    {
        if (System.Console.IsOutputRedirected || !isSelected)
        {
            System.Console.Write(value);
            return;
        }

        var previous = System.Console.ForegroundColor;
        try
        {
            System.Console.ForegroundColor = ConsoleColor.White;
            System.Console.Write(value);
        }
        finally
        {
            System.Console.ForegroundColor = previous;
        }
    }

    private static DateOnly? MinDate(DateOnly? first, DateOnly? second)
        => first is null ? second : second is null ? first : first <= second ? first : second;

    private static DateOnly? MaxDate(DateOnly? first, DateOnly? second)
        => first is null ? second : second is null ? first : first >= second ? first : second;

    private static DateTimeOffset ToStartOfUtcDay(DateOnly date)
        => new(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    private readonly record struct ConsoleDateRange(DateOnly Start, DateOnly End);

    private sealed record ConsoleIndexingRange(
        SemanticIndexRangePreset Preset,
        DateTimeOffset CutoffUtc,
        DateTimeOffset ThroughUtcExclusive,
        bool AutomaticallyIndexNewMessages,
        DateOnly Start,
        DateOnly End);

    private sealed record ConsoleCoverageBucket(
        DateOnly Start,
        DateOnly End,
        int LocalAndCloud,
        int LocalOnly,
        long CloudOnly)
    {
        public long Total => LocalAndCloud + LocalOnly + CloudOnly;
        public char Character => LocalAndCloud > 0 ? '█' : LocalOnly > 0 ? '▓' : CloudOnly > 0 ? '░' : '·';
        public bool Overlaps(ConsoleDateRange range) => Start <= range.End && End >= range.Start;
    }

    private static async Task RunSemanticSearchMenuAsync(
        MailAccount account,
        SemanticIndexAccountState state,
        IWinoAccountApiClient apiClient,
        IIntelligenceMessageContextResolver messageResolver,
        IMailService mailService,
        CancellationToken cancellationToken)
    {
        var mailboxId = state.ServerMailboxId;
        if (mailboxId is null)
        {
            var mailboxes = await apiClient.GetSemanticMailboxesAsync(cancellationToken).ConfigureAwait(false);
            mailboxId = mailboxes.SingleOrDefault(mailbox =>
                mailbox.ProviderType == (int)account.ProviderType &&
                string.Equals(mailbox.Address.Trim(), account.Address.Trim(), StringComparison.OrdinalIgnoreCase))?.MailboxId;
        }
        if (mailboxId is null)
        {
            ConsoleOutput.Warning("This account has no semantic mailbox. Synchronize embeddings first.");
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            ConsoleOutput.Header("\nSemantic search:");
            System.Console.WriteLine("  1. Enter a query");
            System.Console.WriteLine("  0. Back");
            ConsoleOutput.Prompt("Selection: ");
            var selection = ReadSemanticSearchSelection(System.Console.ReadLine(), System.Console.ReadLine);
            if (selection is null)
                return;
            if (selection.Query.Length == 0)
            {
                ConsoleOutput.Warning("Select a listed query or enter a non-empty custom query.");
                continue;
            }

            ConsoleOutput.Muted(selection.UseQueryPlanner
                ? $"Building the semantic query, creating its embedding, and searching for: {selection.Query}"
                : $"Creating query embedding and searching for: {selection.Query}");
            var language = string.IsNullOrWhiteSpace(CultureInfo.CurrentUICulture.Name)
                ? "en-US"
                : CultureInfo.CurrentUICulture.Name;
            var response = await apiClient.SearchIntelligenceAsync(
                new IntelligenceSemanticSearchRequest(
                    selection.Query,
                    [new IntelligenceMailboxSearchScopeDto(mailboxId.Value)],
                    10,
                    null,
                    TimeZoneInfo.Local.Id,
                    language,
                    selection.UseQueryPlanner),
                cancellationToken).ConfigureAwait(false);

            foreach (var omission in response.Mailboxes.Where(item => item.OmissionReason is not null))
                ConsoleOutput.Warning($"Mailbox search omitted: {omission.State} ({omission.OmissionReason})");

            var candidates = await messageResolver.GetCandidatesAsync(account.Id, null, cancellationToken).ConfigureAwait(false);
            var localIds = candidates
                .GroupBy(candidate => candidate.RemoteMessageId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().UniqueId, StringComparer.Ordinal);
            var found = new List<(MailCopy Mail, double Similarity)>();
            foreach (var item in response.Items)
            {
                if (!localIds.TryGetValue(item.RemoteMessageId, out var localId))
                    continue;
                var mail = await mailService.GetSingleMailItemAsync(localId).ConfigureAwait(false);
                if (mail is not null)
                    found.Add((mail, item.Similarity));
            }

            ConsoleOutput.Header($"\nSemantic search results ({found.Count}):");
            for (var index = 0; index < found.Count; index++)
            {
                var result = found[index];
                System.Console.WriteLine($"  {index + 1}. {result.Mail.Subject}");
                ConsoleOutput.Muted($"     {FormatPreview(result.Mail.PreviewText)}");
                ConsoleOutput.Muted($"     Similarity: {result.Similarity:P1}");
            }
            if (found.Count == 0)
                ConsoleOutput.Warning(response.Items.Count == 0
                    ? "The semantic index returned no messages. Synchronize embeddings and try again."
                    : "Search matches could not be resolved to local email records.");
            if (selection.UseQueryPlanner)
            {
                var highConfidenceCount = found.Count(result => result.Similarity >= 0.5d);
                if (highConfidenceCount > 0)
                    ConsoleOutput.Success($"Query builder verification passed: {highConfidenceCount} result(s) have at least 50% similarity.");
                else
                    ConsoleOutput.Warning("Query builder verification failed: no result reached 50% similarity.");
            }
        }
    }

    internal static SemanticSearchSelection? ReadSemanticSearchSelection(string? selection, Func<string?> readCustomQuery)
    {
        var value = selection?.Trim();
        if (value == "0")
            return null;
        if (int.TryParse(value, out var number) && number == 1)
        {
            ConsoleOutput.Prompt("Query: ");
            return new(readCustomQuery()?.Trim() ?? string.Empty, false);
        }
        return new(string.Empty, false);
    }

    private static string FormatPreview(string? preview)
    {
        if (string.IsNullOrWhiteSpace(preview))
            return "(no preview)";
        var normalized = string.Join(' ', preview.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 240 ? normalized : $"{normalized[..237]}...";
    }

    internal sealed record SemanticSearchSelection(string Query, bool UseQueryPlanner);

    private static async Task VerifyLocalMetadataAsync(
        Guid accountId,
        DateTimeOffset? cutoffUtc,
        DateTimeOffset? throughUtcExclusive,
        IIntelligenceMessageContextResolver messageResolver,
        ILocalIntelligenceStore localStore,
        CancellationToken cancellationToken)
    {
        var candidates = await messageResolver.GetCandidatesAsync(
            accountId, cutoffUtc, throughUtcExclusive, cancellationToken).ConfigureAwait(false);
        var documents = await localStore.GetCurrentDocumentsAsync(
            accountId, candidates.Select(static candidate => candidate.RemoteMessageId).ToArray(), cancellationToken)
            .ConfigureAwait(false);
        var missing = candidates
            .Where(candidate => !documents.ContainsKey(candidate.RemoteMessageId))
            .Select(static candidate => candidate.RemoteMessageId)
            .ToList();

        ConsoleOutput.Header("Local WinoIntelligence.db verification:");
        System.Console.WriteLine($"  Eligible messages: {candidates.Count}");
        System.Console.WriteLine($"  Messages with V1 intelligence documents: {documents.Count}");
        System.Console.WriteLine($"  Missing messages: {missing.Count}");
        if (missing.Count > 0)
            throw new InvalidOperationException($"Local metadata is incomplete for {missing.Count} messages.");
        ConsoleOutput.Success("All eligible messages have complete metadata in WinoIntelligence.db.");
    }

    private static async Task<bool> EnsureProcessConsentAsync(
        MailAccount account,
        IWinoAccountApiClient apiClient,
        CancellationToken cancellationToken)
    {
        var consent = await apiClient.GetIntelligenceConsentAsync(cancellationToken).ConfigureAwait(false);
        if (consent.Status == ConsentStatuses.Active &&
            consent.AcceptedPolicyVersion == consent.CurrentPolicyVersion)
        {
            return true;
        }

        if (!Confirm("Approve the current account-wide Wino Intelligence consent?"))
            return false;

        var accepted = await apiClient.AcceptIntelligenceConsentAsync(
            consent.CurrentPolicyVersion,
            ConsentActionSources.ConsentPage,
            cancellationToken).ConfigureAwait(false);
        var isCurrent = accepted.Status == ConsentStatuses.Active &&
                        accepted.AcceptedPolicyVersion == accepted.CurrentPolicyVersion;
        if (isCurrent)
            ConsoleOutput.Success("Wino Intelligence consent approved.");
        else
            ConsoleOutput.Error("The server did not activate Wino Intelligence consent.");
        return isCurrent;
    }

    private static async Task<bool> EnsureProviderAuthenticationAsync(
        MailAccount account,
        IAuthenticationProvider authenticationProvider)
    {
        if (account.ProviderType == MailProviderType.IMAP4)
        {
            ConsoleOutput.Success("IMAP authentication is configured on the account.");
            return true;
        }

        if (account.ProviderType is not (MailProviderType.Outlook or MailProviderType.Gmail))
        {
            ConsoleOutput.Error($"Authentication is not supported for {account.ProviderType} accounts.");
            return false;
        }

        var providerName = account.ProviderType.ToString();
        var authenticator = authenticationProvider.GetAuthenticator(account.ProviderType);
        try
        {
            _ = await authenticator.GetTokenInformationAsync(account).ConfigureAwait(false);
            ConsoleOutput.Success($"{providerName} authentication is ready.");
            return true;
        }
        catch (AuthenticationAttentionException)
        {
            if (!Confirm($"{providerName} requires interactive authentication. Continue?"))
                return false;

            _ = await authenticator.GenerateTokenInformationAsync(account).ConfigureAwait(false);
            ConsoleOutput.Success($"{providerName} authentication completed.");
            return true;
        }
    }

    private static async Task<bool> MonitorJobAsync(
        Guid accountId,
        ISemanticIndexCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        SemanticIndexJobSnapshot? previous = null;
        var phaseTimer = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = coordinator.GetJobSnapshot(accountId);
            var elapsed = phaseTimer.Elapsed;
            ConsoleOutput.Status(
                $"Selected: {snapshot.SelectedMessageCount} | " +
                $"Restored: {snapshot.RestoredMessageCount} | " +
                $"Uploaded: {snapshot.UploadedMessageCount} | " +
                $"Failed: {snapshot.FailedMessageCount} | " +
                $"Status: {snapshot.Status} | " +
                $"Poll elapsed: {elapsed.TotalSeconds:0.00}s" +
                (string.IsNullOrWhiteSpace(snapshot.ErrorCode) ? string.Empty : $" — {snapshot.ErrorCode}"),
                snapshot.Status);
            if (snapshot != previous)
            {
                previous = snapshot;
                phaseTimer.Restart();
            }

            switch (snapshot.Status)
            {
                case SemanticIndexJobStatus.Completed:
                    return true;
                case SemanticIndexJobStatus.Failed:
                case SemanticIndexJobStatus.Cancelled:
                case SemanticIndexJobStatus.PausedForQuota:
                    return false;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
    }

    private static MailAccount? SelectAccount(IReadOnlyList<MailAccount> accounts)
    {
        ConsoleOutput.Header("\nAccounts from Wino database:");
        for (var index = 0; index < accounts.Count; index++)
        {
            System.Console.WriteLine($"  {index + 1}. {FormatAccountSelection(accounts[index])}");
        }
        ConsoleOutput.Prompt("Select an account, or 0 to exit: ");
        if (!int.TryParse(System.Console.ReadLine(), out var selection) || selection == 0)
            return null;
        if (selection < 1 || selection > accounts.Count)
        {
            ConsoleOutput.Warning("Select a listed account.");
            return SelectAccount(accounts);
        }
        return accounts[selection - 1];
    }

    internal static string FormatAccountSelection(MailAccount account)
    {
        var attention = account.AttentionReason == AccountAttentionReason.None
            ? "attention: none"
            : $"ATTENTION: {account.AttentionReason}";
        var intelligence = IsSupportedAccount(account) ? string.Empty : " [intelligence unavailable]";
        return $"{account.Name} <{account.Address}> — {account.ProviderType} [{attention}]{intelligence}";
    }

    private static async Task<bool> InvalidateAuthenticationDataAsync(
        MailAccount account,
        IAccountService accountService,
        ISynchronizationManager synchronizationManager,
        CancellationToken cancellationToken)
    {
        ConsoleOutput.Warning(
            "This immediately removes cached provider credentials. Local mail, calendar, contacts, and Todo data are preserved.");
        ConsoleOutput.Prompt($"Type INVALIDATE to remove authentication data for {account.Address}: ");
        if (!string.Equals(System.Console.ReadLine(), "INVALIDATE", StringComparison.Ordinal))
        {
            ConsoleOutput.Warning("Authentication invalidation cancelled.");
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await synchronizationManager.CancelSynchronizationsAsync(account.Id).ConfigureAwait(false);
        await synchronizationManager.DestroySynchronizerAsync(account.Id).ConfigureAwait(false);
        await accountService.DeleteAccountAuthenticationDataAsync(account.Id).ConfigureAwait(false);
        ConsoleOutput.Success("Authentication data was removed. The account now requires attention.");
        return true;
    }

    private static SemanticIndexRangePreset? SelectRange()
    {
        ConsoleOutput.Header("\nEmbedding range:");
        System.Console.WriteLine("  1. One week");
        System.Console.WriteLine("  2. One month");
        System.Console.WriteLine("  3. One year");
        System.Console.WriteLine("  0. Back");
        ConsoleOutput.Prompt("Selection [1]: ");
        return ParseRangeSelection(System.Console.ReadLine());
    }

    private static ApiEnvironment SelectApiEnvironment()
    {
        ConsoleOutput.Header("Wino Intelligence API:");
        System.Console.WriteLine($"  1. Local — {LocalApiUrl}");
        System.Console.WriteLine($"  2. Production — {ProductionApiUrl}");
        ConsoleOutput.Prompt("Selection [1]: ");
        return System.Console.ReadLine()?.Trim() == "2" ? ApiEnvironment.Production : ApiEnvironment.Local;
    }

    private static void PrintState(SemanticIndexAccountState state)
    {
        var serverHead = state.ServerHead;
        ConsoleOutput.Header("\nCurrent intelligence state:");
        System.Console.WriteLine($"  Enabled: {state.IsEnabled}");
        System.Console.WriteLine($"  Mailbox: {state.ServerMailboxId?.ToString("D") ?? "none"}");
        System.Console.WriteLine($"  Local indexed messages: {state.LocalIndexedMessageCount}");
        System.Console.WriteLine($"  Waiting messages: {state.WaitingMessageCount}");
        System.Console.WriteLine($"  Intelligence version: {serverHead?.IntelligenceVersion ?? "none"}");
        System.Console.WriteLine($"  Index epoch: {serverHead?.IndexEpoch.ToString("D") ?? "none"}");
        System.Console.WriteLine($"  Local revision: {state.LastImportedVersion}");
        System.Console.WriteLine($"  Oldest indexed: {serverHead?.OldestAnalyzedMessageUtc?.ToString("u") ?? "none"}");
        System.Console.WriteLine($"  Newest indexed: {serverHead?.NewestAnalyzedMessageUtc?.ToString("u") ?? "none"}");
        System.Console.WriteLine($"  Storage: {FormatBytes(serverHead?.StorageSizeBytes ?? 0)}");
        System.Console.WriteLine($"  Can download: {state.CanDownload}");
        System.Console.WriteLine($"  Up to date: {state.IsUpToDate}");
    }

    private static bool Confirm(string prompt)
    {
        ConsoleOutput.Prompt($"{prompt} [y/N]: ");
        var response = System.Console.ReadLine()?.Trim();
        return string.Equals(response, "y", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(response, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB"];
        var value = (double)bytes;
        var suffix = 0;
        while (value >= 1024 && suffix < suffixes.Length - 1)
        {
            value /= 1024;
            suffix++;
        }
        return $"{value:0.##} {suffixes[suffix]}";
    }

    private static ConsolePaths ResolvePaths(CommandLineOptions options)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var publisherFolder = options.PublisherFolder ?? Path.Combine(localAppData, PublisherRelativePath);
        var debugApplicationDataFolder = Path.Combine(localAppData, DebugLocalStateRelativePath);
        var previewApplicationDataFolder = Path.Combine(localAppData, PreviewLocalStateRelativePath);
        var applicationDataFolder = options.ApplicationDataFolder ??
            (Directory.Exists(debugApplicationDataFolder) ? debugApplicationDataFolder : previewApplicationDataFolder);
        var tempFolder = Path.Combine(Path.GetTempPath(), "Wino.SmokeTest.Console");
        Directory.CreateDirectory(tempFolder);
        return new ConsolePaths(Path.GetFullPath(publisherFolder), Path.GetFullPath(applicationDataFolder), tempFolder);
    }

    private static bool ValidatePaths(ConsolePaths paths)
    {
        var databasePath = Path.Combine(paths.PublisherFolder, "Wino210.db");
        var tokenCachePath = Path.Combine(paths.ApplicationDataFolder, "OutlookCache.bin");
        var valid = true;
        if (!File.Exists(databasePath))
        {
            ConsoleOutput.Error($"Database not found: {databasePath}");
            valid = false;
        }
        if (!File.Exists(tokenCachePath))
            ConsoleOutput.Warning($"Warning: Outlook token cache not found: {tokenCachePath}. Interactive authentication may be required.");
        if (!Directory.Exists(paths.ApplicationDataFolder))
        {
            ConsoleOutput.Error($"Debug application data folder not found: {paths.ApplicationDataFolder}");
            valid = false;
        }
        return valid;
    }

    private static bool TryParseArguments(string[] args, out CommandLineOptions options, out string? error)
    {
        if (args.Contains("--smoke", StringComparer.OrdinalIgnoreCase))
        {
            if (!SmokeCommandLine.TryParse(args, out var smoke, out error))
            {
                options = default;
                return false;
            }

            options = new CommandLineOptions(
                smoke.PublisherFolder,
                smoke.ApplicationDataFolder,
                null,
                null,
                null,
                null,
                100,
                false,
                false,
                null,
                smoke,
                smoke.AttachmentsFolder);
            return true;
        }

        if (args.Contains("--stress", StringComparer.OrdinalIgnoreCase))
        {
            if (!StressCommandLine.TryParse(args, out var stress, out error))
            {
                options = default;
                return false;
            }
            options = new CommandLineOptions(null, null, null, null, null, null, 100, false, false, stress, null, null);
            return true;
        }

        string? publisher = null;
        string? appData = null;
        var help = false;
        string? dailyBriefingAddress = null;
        string? indexAccountAddress = null;
        string? todoGroupProbeAddress = null;
        string? indexFolderName = null;
        string? attachmentsFolder = null;
        var indexMessageCount = 100;
        var resetIntelligence = false;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--help" or "-h":
                    help = true;
                    break;
                case "--publisher-folder" when index + 1 < args.Length:
                    publisher = args[++index];
                    break;
                case "--app-data-folder" when index + 1 < args.Length:
                    appData = args[++index];
                    break;
                case "--daily-briefing" when index + 1 < args.Length:
                    dailyBriefingAddress = args[++index];
                    break;
                case "--index-account" when index + 1 < args.Length:
                    indexAccountAddress = args[++index];
                    break;
                case "--todo-groups" when index + 1 < args.Length:
                    todoGroupProbeAddress = args[++index];
                    break;
                case "--folder" when index + 1 < args.Length:
                    indexFolderName = args[++index];
                    break;
                case "--top" when index + 1 < args.Length &&
                    int.TryParse(args[index + 1], out var parsedCount) && parsedCount > 0:
                    indexMessageCount = parsedCount;
                    index++;
                    break;
                case "--reset-intelligence":
                    resetIntelligence = true;
                    break;
                case "--attachments-folder" when index + 1 < args.Length:
                    attachmentsFolder = Path.GetFullPath(args[++index]);
                    break;
                default:
                    options = default;
                    error = $"Unknown or incomplete argument: {args[index]}";
                    return false;
            }
        }
        options = new CommandLineOptions(
            publisher,
            appData,
            dailyBriefingAddress,
            indexAccountAddress,
            todoGroupProbeAddress,
            indexFolderName,
            indexMessageCount,
            resetIntelligence,
            help,
            null,
            null,
            attachmentsFolder);
        error = null;
        return true;
    }

    private static void PrintHelp()
    {
        System.Console.WriteLine("Wino smoke test and intelligence console");
        System.Console.WriteLine("  --publisher-folder <path>  Override the WinoShared publisher folder");
        System.Console.WriteLine("  --app-data-folder <path>   Override the Debug package LocalState folder");
        System.Console.WriteLine("  --daily-briefing <email>   Report today's and yesterday's briefing facts for an account");
        System.Console.WriteLine("  --index-account <email>    Reconcile and index a real account without menus");
        System.Console.WriteLine("  --folder <name-or-id>      Folder for --index-account (default: Inbox)");
        System.Console.WriteLine("  --top <count>              Newest messages for --index-account (default: 100)");
        System.Console.WriteLine("  --reset-intelligence       Delete local and server intelligence before indexing");
        System.Console.WriteLine("  --smoke --account <email>  Run the unattended live-account smoke suite");
        System.Console.WriteLine("          [--report-to <email>] [--attachments-folder <path>]");
        System.Console.WriteLine("  --stress                   Run the non-interactive intelligence stress harness");
        System.Console.WriteLine("  Stress: --environment local|production --account <email> --output <folder>");
        System.Console.WriteLine("          [--profile realistic|database|ai] [--start-rps 1] [--max-rps 256]");
        System.Console.WriteLine("          [--max-concurrency 512] [--stage-duration 5] [--sustain-duration 60]");
        System.Console.WriteLine("          [--ai-request-limit N] [--confirm-production-stress]");
        System.Console.WriteLine("  --todo-groups <address>    Probe the substrate To Do foldergroups API");
        System.Console.WriteLine("  --help                     Show help");
    }
}

internal enum ApiEnvironment
{
    Local,
    Production,
}

internal readonly record struct CommandLineOptions(
    string? PublisherFolder,
    string? ApplicationDataFolder,
    string? DailyBriefingAddress,
    string? IndexAccountAddress,
    string? TodoGroupProbeAddress,
    string? IndexFolderName,
    int IndexMessageCount,
    bool ResetIntelligence,
    bool ShowHelp,
    StressOptions? Stress,
    SmokeCommandLineOptions? Smoke,
    string? AttachmentsFolder);
internal sealed record ConsolePaths(string PublisherFolder, string ApplicationDataFolder, string TempFolder);
