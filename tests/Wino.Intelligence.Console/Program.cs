using System.Net;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wino.Core;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.SemanticIndexing;
using Wino.Core.Services;
using Wino.Mail.Contracts.Intelligence;
using Wino.Mail.AI.Abstractions;
using Wino.Services;

namespace Wino.Intelligence.ConsoleApp;

internal static class Program
{
    private const string LocalApiUrl = "https://localhost:7204/";
    private const string ProductionApiUrl = "https://api.winomail.app/";
    private const string PublisherRelativePath = @"Publishers\mhdqskaa8n2sj\WinoShared";
    private const string DebugLocalStateRelativePath = @"Packages\58272BurakKSE.WinoMailPreview.Debug_mhdqskaa8n2sj\LocalState";
    private const string PreviewLocalStateRelativePath = @"Packages\58272BurakKSE.WinoMailPreview_mhdqskaa8n2sj\LocalState";
    private static readonly IReadOnlyList<SemanticSearchPreset> SemanticSearchPresets =
    [
        new("Rust developer recruiter", "A recruiter is looking for a Rust software developer or Rust engineer for a job opportunity.", false),
        new("Wino Mail NuGet publishing", "Notifications from NuGet about publishing Wino Mail packages.", false),
        new("Complexcity waste segregation", "Wiadomość od Complexcity o nieprawidłowej segregacji śmieci lub odpadów.", false),
        new("Query builder: Messages from Upwork", "Messages from Upwork", true),
    ];

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

        var apiEnvironment = SelectApiEnvironment();
        var apiUri = GetApiUri(apiEnvironment);
        var paths = ResolvePaths(options);
        if (!ValidatePaths(paths))
            return 2;

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
            ConsoleOutput.Header($"\nAPI: {apiEnvironment} ({apiUri})");
            System.Console.WriteLine($"Database folder: {paths.PublisherFolder}");
            System.Console.WriteLine($"Application data: {paths.ApplicationDataFolder}");
            ConsoleOutput.Warning("Keep Wino Mail closed while this tool is using its databases.\n");

            await using var services = CreateServices(paths, apiUri, apiEnvironment);
            await InitializeServicesAsync(services, cancellation.Token).ConfigureAwait(false);
            return await RunAsync(services, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            ConsoleOutput.Warning("Operation cancelled.");
            return 1;
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
        => account.ProviderType == MailProviderType.Outlook;

    internal static bool HasIntelligence(SemanticIndexAccountState state)
        => state.ServerIndex?.StorageSizeBytes > 0 ||
           state.ServerIndex?.OldestIndexedAtUtc is not null ||
           state.LastImportedVersion > 0;

    internal static bool HasIncompleteIntelligence(SemanticIndexAccountState state)
        => state.WaitingMessageCount > 0 ||
           string.Equals(state.IntelligenceState?.IndexState, "rebuilding", StringComparison.OrdinalIgnoreCase);

    internal static SemanticIndexRangePreset? ParseRangeSelection(string? selection)
        => selection?.Trim() switch
        {
            "" or null or "1" => SemanticIndexRangePreset.OneWeek,
            "2" => SemanticIndexRangePreset.OneMonth,
            "3" => SemanticIndexRangePreset.OneYear,
            _ => null,
        };

    private static ServiceProvider CreateServices(ConsolePaths paths, Uri apiUri, ApiEnvironment environment)
    {
        var nativeAppService = new ConsoleNativeAppService(paths.ApplicationDataFolder);
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
            return new HttpClient(handler) { BaseAddress = apiUri };
        });
        serviceCollection.AddSingleton<IWinoAccountApiClient>(provider =>
        {
            return new WinoAccountApiClient(
                provider.GetRequiredService<IDatabaseService>(),
                provider.GetRequiredService<HttpClient>(),
                provider.GetRequiredService<Wino.Mail.AI.Abstractions.IContentEnvelopeEncryptor>(),
                provider.GetRequiredService<ITranslationService>());
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

    private static async Task InitializeServicesAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        await services.GetRequiredService<IDatabaseService>().InitializeAsync().ConfigureAwait(false);
        await services.GetRequiredService<ITranslationService>().InitializeAsync().ConfigureAwait(false);
        await services.GetRequiredService<SynchronizationManagerInitializer>().InitializeAsync().ConfigureAwait(false);
        await services.GetRequiredService<ISemanticIndexCoordinator>().InitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var accountService = services.GetRequiredService<IAccountService>();
        var coordinator = services.GetRequiredService<ISemanticIndexCoordinator>();
        var apiClient = services.GetRequiredService<IWinoAccountApiClient>();
        var authenticationProvider = services.GetRequiredService<IAuthenticationProvider>();
        var messageResolver = services.GetRequiredService<IIntelligenceMessageContextResolver>();
        var localStore = services.GetRequiredService<ILocalIntelligenceStore>();
        var mailService = services.GetRequiredService<IMailService>();

        while (!cancellationToken.IsCancellationRequested)
        {
            var accounts = await accountService.GetAccountsAsync().ConfigureAwait(false);
            if (accounts.Count == 0)
            {
                ConsoleOutput.Warning("No mail accounts were found in Wino200.db.");
                return 1;
            }

            var selected = SelectAccount(accounts);
            if (selected is null)
                return 0;

            try
            {
                await RunAccountAsync(selected, accountService, coordinator, apiClient, authenticationProvider,
                        messageResolver, localStore, mailService, cancellationToken)
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

    private static async Task RunAccountAsync(
        MailAccount account,
        IAccountService accountService,
        ISemanticIndexCoordinator coordinator,
        IWinoAccountApiClient apiClient,
        IAuthenticationProvider authenticationProvider,
        IIntelligenceMessageContextResolver messageResolver,
        ILocalIntelligenceStore localStore,
        IMailService mailService,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var state = await coordinator.GetStateAsync(account.Id, cancellationToken).ConfigureAwait(false);
            PrintState(state);
            ConsoleOutput.Header("\nAccount actions:");
            System.Console.WriteLine("  1. Semantic search");
            System.Console.WriteLine("  2. Synchronize embeddings and intelligence");
            System.Console.WriteLine("  0. Back");
            ConsoleOutput.Prompt("Selection [1]: ");
            switch (System.Console.ReadLine()?.Trim())
            {
                case "" or null or "1":
                    await RunSemanticSearchMenuAsync(
                        account, state, apiClient, messageResolver, mailService, cancellationToken).ConfigureAwait(false);
                    break;
                case "2":
                    await SynchronizeIntelligenceAsync(
                        account, accountService, coordinator, apiClient, authenticationProvider,
                        messageResolver, localStore, cancellationToken).ConfigureAwait(false);
                    break;
                case "0":
                    return;
                default:
                    ConsoleOutput.Warning("Select a listed action.");
                    break;
            }
        }
    }

    private static async Task SynchronizeIntelligenceAsync(
        MailAccount account,
        IAccountService accountService,
        ISemanticIndexCoordinator coordinator,
        IWinoAccountApiClient apiClient,
        IAuthenticationProvider authenticationProvider,
        IIntelligenceMessageContextResolver messageResolver,
        ILocalIntelligenceStore localStore,
        CancellationToken cancellationToken)
    {
        var state = await coordinator.GetStateAsync(account.Id, cancellationToken).ConfigureAwait(false);
        var hasIntelligence = HasIntelligence(state);
        var hasIncompleteIntelligence = HasIncompleteIntelligence(state);

        if (hasIntelligence || hasIncompleteIntelligence)
        {
            var deletePrompt = hasIntelligence
                ? "Delete this account's server and local intelligence?"
                : "An unfinished indexing job exists. Delete its server and local intelligence?";
            if (Confirm(deletePrompt))
            {
                await coordinator.DeleteIndexAsync(account.Id, cancellationToken).ConfigureAwait(false);
                account.Preferences.IsSemanticIndexingEnabled = false;
                await accountService.UpdateAccountAsync(account).ConfigureAwait(false);
                ConsoleOutput.Success("Intelligence deleted. Process consent was preserved.");
                state = await coordinator.GetStateAsync(account.Id, cancellationToken).ConfigureAwait(false);
                PrintState(state);
            }
            else
            {
                if (Confirm("Reset only this account's local intelligence cache and download the server copy again?"))
                {
                    await coordinator.DeleteLocalIndexAsync(account.Id, cancellationToken).ConfigureAwait(false);
                    ConsoleOutput.Success("Local intelligence cache deleted. Server intelligence was preserved.");
                }
                else
                {
                    ConsoleOutput.Muted("Keeping existing immutable intelligence. Start-time delta resolution will resume only missing work.");
                }
            }
        }

        if (!await EnsureProcessConsentAsync(account, apiClient, cancellationToken).ConfigureAwait(false))
            return;

        var preset = SelectRange();
        if (preset is null)
            return;

        ConsoleOutput.Muted("Calculating embedding plan...");
        var plan = await coordinator.CalculatePlanAsync(account.Id, preset.Value, automaticallyIndexNewMessages: true, cancellationToken)
            .ConfigureAwait(false);
        PrintPlan(plan);
        if (plan.RequiresReset)
        {
            if (!Confirm("The existing range conflicts with this plan. Delete the current intelligence before continuing?"))
                return;

            await coordinator.DeleteIndexAsync(account.Id, cancellationToken).ConfigureAwait(false);
            account.Preferences.IsSemanticIndexingEnabled = false;
            await accountService.UpdateAccountAsync(account).ConfigureAwait(false);
            ConsoleOutput.Success("Conflicting intelligence deleted. Process consent was preserved.");
            if (!await EnsureProcessConsentAsync(account, apiClient, cancellationToken).ConfigureAwait(false))
                return;
            plan = await coordinator.CalculatePlanAsync(account.Id, preset.Value, automaticallyIndexNewMessages: true, cancellationToken)
                .ConfigureAwait(false);
            PrintPlan(plan);
        }
        if (!Confirm("Start embedding synchronization with this plan?"))
            return;

        if (!await EnsureOutlookAuthenticationAsync(account, authenticationProvider).ConfigureAwait(false))
            return;

        var wasEnabled = account.Preferences.IsSemanticIndexingEnabled;
        try
        {
            if (!wasEnabled)
            {
                account.Preferences.IsSemanticIndexingEnabled = true;
                await accountService.UpdateAccountAsync(account).ConfigureAwait(false);
            }

            await coordinator.StartIndexingAsync(account.Id, plan, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (!wasEnabled)
            {
                account.Preferences.IsSemanticIndexingEnabled = false;
                await accountService.UpdateAccountAsync(account).ConfigureAwait(false);
            }
            throw;
        }

        var completed = await MonitorJobAsync(account.Id, coordinator, cancellationToken).ConfigureAwait(false);
        if (!completed)
            return;

        ConsoleOutput.Header("Retrieving intelligence metadata...");
        var progress = new Progress<SemanticIndexingProgress>(value =>
            System.Console.WriteLine($"Metadata: {value.CompletedMessageCount}/{value.TotalMessageCount}"));
        var finalState = await coordinator.DownloadAvailableIntelligenceAsync(account.Id, progress, cancellationToken)
            .ConfigureAwait(false);
        PrintState(finalState);
        await VerifyLocalMetadataAsync(account.Id, plan, messageResolver, localStore, cancellationToken).ConfigureAwait(false);
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
            ConsoleOutput.Header("\nSemantic search queries:");
            for (var index = 0; index < SemanticSearchPresets.Count; index++)
            {
                System.Console.WriteLine($"  {index + 1}. {SemanticSearchPresets[index].Name}");
                ConsoleOutput.Muted($"     {SemanticSearchPresets[index].Query}");
            }
            System.Console.WriteLine($"  {SemanticSearchPresets.Count + 1}. Enter my own query");
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
        if (int.TryParse(value, out var number) && number >= 1 && number <= SemanticSearchPresets.Count)
        {
            var preset = SemanticSearchPresets[number - 1];
            return new(preset.Query, preset.UseQueryPlanner);
        }
        if (number == SemanticSearchPresets.Count + 1)
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

    private sealed record SemanticSearchPreset(string Name, string Query, bool UseQueryPlanner);

    internal sealed record SemanticSearchSelection(string Query, bool UseQueryPlanner);

    private static async Task VerifyLocalMetadataAsync(
        Guid accountId,
        SemanticIndexPlan plan,
        IIntelligenceMessageContextResolver messageResolver,
        ILocalIntelligenceStore localStore,
        CancellationToken cancellationToken)
    {
        var requiredCapabilities = new HashSet<string>(StringComparer.Ordinal)
        {
            IntelligenceCapabilityIds.SmartLabels,
            IntelligenceCapabilityIds.BriefingFact,
        };
        var candidates = await messageResolver.GetCandidatesAsync(
            accountId, plan.CutoffUtc, plan.ThroughUtcExclusive, cancellationToken).ConfigureAwait(false);
        var artifactsByMessage = new Dictionary<string, IReadOnlyList<IntelligenceArtifactDto>>(StringComparer.Ordinal);
        var briefingIds = new HashSet<Guid>();
        foreach (var candidate in candidates)
        {
            var artifacts = await localStore.GetCurrentArtifactsAsync(
                accountId, candidate.RemoteMessageId, cancellationToken).ConfigureAwait(false);
            artifactsByMessage[candidate.RemoteMessageId] = artifacts;
            var fact = artifacts.FirstOrDefault(artifact =>
                !artifact.IsDeleted && artifact.Capability == IntelligenceCapability.BriefingFact)?.BriefingFact;
            if (fact is not null)
                briefingIds.Add(fact.BriefingId);
        }
        var headlines = await localStore.GetBriefingHeadlinesAsync(accountId, briefingIds, cancellationToken).ConfigureAwait(false);
        var complete = 0;
        var artifactCount = headlines.Count;
        var missing = new List<string>();
        foreach (var candidate in candidates)
        {
            var artifacts = artifactsByMessage[candidate.RemoteMessageId];
            var capabilities = artifacts.Where(artifact => !artifact.IsDeleted)
                .Select(artifact => IntelligenceCapabilityIds.GetStorageId(artifact.Capability))
                .ToHashSet(StringComparer.Ordinal);
            artifactCount += artifacts.Count;
            var briefingId = artifacts.FirstOrDefault(artifact =>
                !artifact.IsDeleted && artifact.Capability == IntelligenceCapability.BriefingFact)?.BriefingFact?.BriefingId;
            if (requiredCapabilities.IsSubsetOf(capabilities) &&
                briefingId is { } id && headlines.ContainsKey(id))
                complete++;
            else
                missing.Add(candidate.RemoteMessageId);
        }

        ConsoleOutput.Header("Local WinoIntelligence.db verification:");
        System.Console.WriteLine($"  Eligible messages: {candidates.Count}");
        System.Console.WriteLine($"  Messages with all required metadata: {complete}");
        System.Console.WriteLine($"  Current metadata artifacts: {artifactCount}");
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
        var consents = await apiClient.GetProcessConsentsAsync(cancellationToken).ConfigureAwait(false);
        var current = consents.Mailboxes.FirstOrDefault(item =>
            item.ProviderType == (int)account.ProviderType &&
            string.Equals(item.Address.Trim(), account.Address.Trim(), StringComparison.OrdinalIgnoreCase));
        if (current is not null && current.Status == ConsentStatuses.Active &&
            current.AcceptedPolicyVersion == current.CurrentPolicyVersion)
        {
            return true;
        }

        if (!Confirm($"Approve the current mail-processing consent for {account.Address}?"))
            return false;

        var mailbox = await apiClient.EnsureSemanticMailboxAsync(account.Address, (int)account.ProviderType, cancellationToken)
            .ConfigureAwait(false);
        var accepted = await apiClient.AcceptProcessConsentAsync(
            mailbox.MailboxId,
            consents.CurrentPolicyVersion,
            ConsentActionSources.IntelligenceEnable,
            cancellationToken).ConfigureAwait(false);
        var isCurrent = accepted.Status == ConsentStatuses.Active &&
                        accepted.AcceptedPolicyVersion == accepted.CurrentPolicyVersion;
        if (isCurrent)
            ConsoleOutput.Success("Process consent approved.");
        else
            ConsoleOutput.Error("The server did not activate process consent.");
        return isCurrent;
    }

    private static async Task<bool> EnsureOutlookAuthenticationAsync(
        MailAccount account,
        IAuthenticationProvider authenticationProvider)
    {
        var authenticator = authenticationProvider.GetAuthenticator(MailProviderType.Outlook);
        try
        {
            _ = await authenticator.GetTokenInformationAsync(account).ConfigureAwait(false);
            ConsoleOutput.Success("Outlook authentication is ready.");
            return true;
        }
        catch (AuthenticationAttentionException)
        {
            if (!Confirm("Outlook requires interactive Microsoft authentication. Continue?"))
                return false;

            _ = await authenticator.GenerateTokenInformationAsync(account).ConfigureAwait(false);
            ConsoleOutput.Success("Outlook authentication completed.");
            return true;
        }
    }

    private static async Task<bool> MonitorJobAsync(
        Guid accountId,
        ISemanticIndexCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        SemanticIndexJobSnapshot? previous = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = coordinator.GetJobSnapshot(accountId);
            if (snapshot != previous)
            {
                ConsoleOutput.Status(
                    $"Embedding: {snapshot.EmbeddingProcessedMessageCount}/{snapshot.TotalMessageCount} " +
                    $"({snapshot.CompletedMessageCount} succeeded, {snapshot.EmbeddingFailedMessageCount} failed) | " +
                    $"Metadata: {snapshot.MetadataProcessedMessageCount}/{snapshot.TotalMessageCount} " +
                    $"({snapshot.MetadataCompletedMessageCount} succeeded, {snapshot.MetadataFailedMessageCount} failed) | " +
                    $"Status: {snapshot.Status}" +
                    (string.IsNullOrWhiteSpace(snapshot.ErrorCode) ? string.Empty : $" — {snapshot.ErrorCode}"),
                    snapshot.Status);
                previous = snapshot;
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

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }
    }

    private static MailAccount? SelectAccount(IReadOnlyList<MailAccount> accounts)
    {
        ConsoleOutput.Header("\nAccounts from Wino database:");
        for (var index = 0; index < accounts.Count; index++)
        {
            var supported = IsSupportedAccount(accounts[index]) ? string.Empty : " [unsupported]";
            System.Console.WriteLine($"  {index + 1}. {accounts[index].Name} <{accounts[index].Address}> — {accounts[index].ProviderType}{supported}");
        }
        ConsoleOutput.Prompt("Select an Outlook account, or 0 to exit: ");
        if (!int.TryParse(System.Console.ReadLine(), out var selection) || selection == 0)
            return null;
        if (selection < 1 || selection > accounts.Count || !IsSupportedAccount(accounts[selection - 1]))
        {
            ConsoleOutput.Warning("Select a listed Outlook account.");
            return SelectAccount(accounts);
        }
        return accounts[selection - 1];
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
        var serverIndex = state.ServerIndex;
        ConsoleOutput.Header("\nCurrent intelligence state:");
        System.Console.WriteLine($"  Enabled: {state.IsEnabled}");
        System.Console.WriteLine($"  Mailbox: {state.ServerMailboxId?.ToString("D") ?? "none"}");
        System.Console.WriteLine($"  Local indexed messages: {state.LocalIndexedMessageCount}");
        System.Console.WriteLine($"  Waiting messages: {state.WaitingMessageCount}");
        System.Console.WriteLine($"  Local revision: {state.LastImportedVersion}");
        System.Console.WriteLine($"  Oldest indexed: {serverIndex?.OldestIndexedAtUtc?.ToString("u") ?? "none"}");
        System.Console.WriteLine($"  Newest indexed: {serverIndex?.NewestIndexedAtUtc?.ToString("u") ?? "none"}");
        System.Console.WriteLine($"  Storage: {FormatBytes(serverIndex?.StorageSizeBytes ?? 0)}");
        System.Console.WriteLine($"  Can download: {state.CanDownload}");
        System.Console.WriteLine($"  Up to date: {state.IsUpToDate}");
    }

    private static void PrintPlan(SemanticIndexPlan plan)
    {
        ConsoleOutput.Header("\nEmbedding plan:");
        System.Console.WriteLine($"  Range: {plan.RangePreset}");
        System.Console.WriteLine($"  Cutoff: {plan.CutoffUtc?.ToString("u") ?? "none"}");
        System.Console.WriteLine($"  Eligible messages: {plan.EligibleMessageCount}");
        System.Console.WriteLine($"  Messages needing embeddings: {plan.MissingMessageCount}");
        System.Console.WriteLine($"  Estimated duration: {plan.EstimatedDuration:g}");
        System.Console.WriteLine($"  Automatically index new messages: {plan.AutomaticallyIndexNewMessages}");
        System.Console.WriteLine($"  Requires reset: {plan.RequiresReset}");
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
        var tempFolder = Path.Combine(Path.GetTempPath(), "Wino.Intelligence.Console");
        Directory.CreateDirectory(tempFolder);
        return new ConsolePaths(Path.GetFullPath(publisherFolder), Path.GetFullPath(applicationDataFolder), tempFolder);
    }

    private static bool ValidatePaths(ConsolePaths paths)
    {
        var databasePath = Path.Combine(paths.PublisherFolder, "Wino200.db");
        var tokenCachePath = Path.Combine(paths.PublisherFolder, "OutlookCache.bin");
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
        string? publisher = null;
        string? appData = null;
        var help = false;
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
                default:
                    options = default;
                    error = $"Unknown or incomplete argument: {args[index]}";
                    return false;
            }
        }
        options = new CommandLineOptions(publisher, appData, help);
        error = null;
        return true;
    }

    private static void PrintHelp()
    {
        System.Console.WriteLine("Wino Outlook intelligence test console");
        System.Console.WriteLine("  --publisher-folder <path>  Override the WinoShared publisher folder");
        System.Console.WriteLine("  --app-data-folder <path>   Override the Debug package LocalState folder");
        System.Console.WriteLine("  --help                     Show help");
    }
}

internal enum ApiEnvironment
{
    Local,
    Production,
}

internal readonly record struct CommandLineOptions(string? PublisherFolder, string? ApplicationDataFolder, bool ShowHelp);
internal sealed record ConsolePaths(string PublisherFolder, string ApplicationDataFolder, string TempFolder);
