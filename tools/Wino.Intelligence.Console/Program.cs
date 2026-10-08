using System.Diagnostics;
using Serilog;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Intelligence.ConsoleApp.Hosting;
using Wino.Intelligence.ConsoleApp.Scenarios;

namespace Wino.Intelligence.ConsoleApp;

internal static class Program
{
    private const string DefaultPackageFamilyName = "58272BurakKSE.WinoMailPreview_mhdqskaa8n2sj";
    private const string PublisherRelativePath = @"Publishers\mhdqskaa8n2sj\WinoShared";

    private static readonly Scenario[] Scenarios =
    [
        new("sync", "Synchronize mail", "Run the provider synchronizer (initial or delta, as it decides) and time it.", SyncScenario.RunAsync),
        new("index", "Index the newest inbox mail", "Consent, add-on, enable, plan and submit, then follow the job to completion.", IndexingScenarios.IndexInboxAsync),
        new("delete", "Delete index data", "Cancel jobs, delete local results and turn indexing off for this account.", IndexingScenarios.DeleteIndexAsync),
        new("revoke", "Decline consent", "Revoke consent on the server and clear local intelligence for every account.", ConsentScenarios.RevokeAsync),
        new("status", "Status dashboard", "Wino account, add-on, quota, consent, mailbox id, result key, local and server jobs.", InspectionScenarios.StatusAsync),
        new("analyze", "Analyze one message", "Single-message route (messages:analyze) with both artifacts printed.", IndexingScenarios.AnalyzeOneAsync),
        new("poll", "Poll or resume jobs", "Collect results for unfinished jobs, as the app does after a restart.", IndexingScenarios.PollAsync),
        new("jobs", "Cancel or re-poll jobs", "Cancel all or one job, or re-poll one job.", IndexingScenarios.ControlJobsAsync),
        new("artifacts", "Show results and briefing", "Label, priority and headline results, and the daily briefing cards.", InspectionScenarios.ArtifactsAsync),
        new("mailboxes", "Register mailboxes on the server", "Export the mailbox list so each account gets a server mailbox id.", InspectionScenarios.RegisterMailboxesAsync),
        new("reindex", "Re-index the inbox", "Delete index data, then index the inbox again, with one total time.", IndexingScenarios.ReindexInboxAsync),
        new("index-folder", "Index any folder", "Pick a folder and a count (over 1000 splits into several jobs).", IndexingScenarios.IndexCustomFolderAsync),
        new("consent", "Review or accept consent", "Show consent and accept the current policy if needed.", ConsentScenarios.ReviewAsync),
    ];

    private static CancellationTokenSource? _scenarioCancellation;

    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        if (!ConsoleArguments.TryParse(args, out var arguments, out var parseError))
        {
            ConsoleOutput.Error(parseError);
            ConsoleArguments.PrintHelp(Scenarios);
            return 2;
        }

        if (arguments.ShowHelp)
        {
            ConsoleArguments.PrintHelp(Scenarios);
            return 0;
        }

        Console.OutputEncoding = System.Text.Encoding.UTF8;
        ConsoleOutput.AssumeYes = arguments.AssumeYes;
        if (arguments.LogFile is { } logFile)
        {
            // The services log through Serilog; this sends their lines (timings included) to a file.
            Serilog.Log.Logger = new Serilog.LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.File(logFile, outputTemplate: "{Timestamp:HH:mm:ss.fff} {SourceContext} {Message:lj}{NewLine}{Exception}")
                .CreateLogger();
        }
        var packageFamilyName = arguments.PackageFamilyName ?? DefaultPackageFamilyName;
        if (!AppRunningGuard.TryAcquire(packageFamilyName, out var guard, out var guardError))
        {
            ConsoleOutput.Error(guardError);
            return 2;
        }

        using var activeGuard = guard;
        var paths = ResolvePaths(arguments, packageFamilyName);
        if (!File.Exists(Path.Combine(paths.ApplicationDataFolder, "Wino210.db")))
        {
            ConsoleOutput.Error($"Wino210.db was not found in {paths.ApplicationDataFolder}.");
            return 2;
        }

        Console.CancelKeyPress += OnCancelKeyPress;

        var apiTarget = arguments.ApiTarget ?? AskApiTarget();
        ConsoleOutput.Header("Wino Intelligence console");
        ConsoleOutput.KeyValue("API", $"{apiTarget} ({ConsoleServices.GetApiUri(apiTarget)})");
        ConsoleOutput.KeyValue("Data folder", paths.ApplicationDataFolder);
        ConsoleOutput.Warning("  This console reads and writes the app's real databases.");
        await ConsoleServices.ProbeApiAsync(apiTarget);

        await using var services = ConsoleServices.Create(paths, apiTarget);
        try
        {
            var startup = Stopwatch.StartNew();
            await ConsoleServices.InitializeAsync(services);
            ConsoleOutput.Muted($"  Startup: {ConsoleOutput.Elapsed(startup.Elapsed)}");

            using var synchronization = new SynchronizationHost(
                services.GetRequiredService<ISynchronizationManager>(),
                services.GetRequiredService<IAccountService>());
            var context = new ScenarioContext(services, apiTarget, synchronization);

            if (await WinoAccountStatus.PrintAsync(context, CancellationToken.None) is null)
                return 2;

            return await RunAsync(context, arguments);
        }
        catch (Exception exception)
        {
            ConsoleOutput.Error($"Fatal: {exception}");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= OnCancelKeyPress;
            await ConsoleServices.ShutdownAsync(services);
            await Serilog.Log.CloseAndFlushAsync();
        }
    }

    private static async Task<int> RunAsync(ScenarioContext context, ConsoleArguments arguments)
    {
        var accounts = await context.Get<IAccountService>().GetAccountsAsync() ?? [];
        if (accounts.Count == 0)
        {
            ConsoleOutput.Error("The database has no mail accounts.");
            return 2;
        }

        var account = arguments.AccountAddress is { } address
            ? accounts.FirstOrDefault(candidate => string.Equals(candidate.Address, address, StringComparison.OrdinalIgnoreCase))
            : await ChooseAccountAsync(context, accounts);
        if (account is null)
        {
            if (arguments.AccountAddress is not null)
                ConsoleOutput.Error($"No mail account has the address {arguments.AccountAddress}.");
            return arguments.AccountAddress is null ? 0 : 2;
        }

        context.Select(account);

        if (arguments.ScenarioKey is { } key)
        {
            var scenario = Scenarios.FirstOrDefault(candidate => string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase));
            if (scenario is null)
            {
                ConsoleOutput.Error($"Unknown scenario '{key}'.");
                return 2;
            }

            return await RunScenarioAsync(context, scenario) ? 0 : 1;
        }

        while (true)
        {
            ConsoleOutput.Header($"\n{context.Account.Name} <{context.Account.Address}> ({context.Account.ProviderType})");
            for (var index = 0; index < Scenarios.Length; index++)
                ConsoleOutput.Info($"  {index + 1,2}. {Scenarios[index].Title,-34} {Scenarios[index].Description}");
            ConsoleOutput.Info("   a. Switch account");
            ConsoleOutput.Info("   q. Quit");

            var choice = ConsoleOutput.Ask("Choose", "q").ToLowerInvariant();
            if (choice is "q" or "quit" or "exit")
                return 0;

            if (choice == "a")
            {
                var next = await ChooseAccountAsync(context, await context.Get<IAccountService>().GetAccountsAsync() ?? []);
                if (next is not null)
                    context.Select(next);
                continue;
            }

            var selected = int.TryParse(choice, out var number) && number >= 1 && number <= Scenarios.Length
                ? Scenarios[number - 1]
                : Scenarios.FirstOrDefault(candidate => string.Equals(candidate.Key, choice, StringComparison.OrdinalIgnoreCase));
            if (selected is null)
            {
                ConsoleOutput.Warning("Unknown choice.");
                continue;
            }

            await RunScenarioAsync(context, selected);
        }
    }

    /// <summary>Runs one scenario with its own Ctrl+C scope and reports how long it took.</summary>
    private static async Task<bool> RunScenarioAsync(ScenarioContext context, Scenario scenario)
    {
        using var cancellation = new CancellationTokenSource();
        _scenarioCancellation = cancellation;
        var clock = Stopwatch.StartNew();

        try
        {
            await scenario.RunAsync(context, cancellation.Token);
            ConsoleOutput.Muted($"'{scenario.Title}' took {ConsoleOutput.Elapsed(clock.Elapsed)}.");
            return true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            ConsoleOutput.Warning($"'{scenario.Title}' cancelled after {ConsoleOutput.Elapsed(clock.Elapsed)}.");
            return false;
        }
        catch (Exception exception)
        {
            ConsoleOutput.Error($"'{scenario.Title}' failed after {ConsoleOutput.Elapsed(clock.Elapsed)}: {IntelligenceFlow.DescribeError(exception)}");

            // API and service failures arrive as InvalidOperationException carrying an error code;
            // anything else is unexpected and worth its stack.
            if (exception is not (InvalidOperationException or HttpRequestException))
                ConsoleOutput.Muted(exception.ToString());
            return false;
        }
        finally
        {
            _scenarioCancellation = null;
        }
    }

    private static async Task<MailAccount?> ChooseAccountAsync(ScenarioContext context, IReadOnlyList<MailAccount> accounts)
    {
        var folderService = context.Get<IFolderService>();
        var descriptions = new List<string>(accounts.Count);
        foreach (var account in accounts)
        {
            var folders = await folderService.GetFoldersAsync(account.Id);
            var lastSynchronized = folders.Max(static folder => folder.LastSynchronizedDate);
            descriptions.Add($"{account.Name,-20} {account.Address,-36} {account.ProviderType,-8} " +
                             $"intelligence {(account.Preferences.IsSemanticIndexingEnabled ? "on " : "off")}  " +
                             $"{folders.Count,3} folders  last sync {lastSynchronized?.ToLocalTime().ToString("g") ?? "never"}" +
                             (account.AttentionReason != Wino.Core.Domain.Enums.AccountAttentionReason.None ? $"  needs attention: {account.AttentionReason}" : string.Empty));
        }

        var index = ConsoleOutput.Choose("Mail accounts", descriptions, static description => description);
        return index < 0 ? null : accounts[index];
    }

    private static ApiTarget AskApiTarget()
    {
        ConsoleOutput.Header("Which Wino API?");
        ConsoleOutput.Info($"  1. Local       {ConsoleServices.LocalApiUrl}");
        ConsoleOutput.Info($"  2. Production  {ConsoleServices.ProductionApiUrl}");
        return ConsoleOutput.AskNumber("Choose", 1, 1, 2) == 2 ? ApiTarget.Production : ApiTarget.Local;
    }

    private static ConsolePaths ResolvePaths(ConsoleArguments arguments, string packageFamilyName)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var packageFolder = Path.Combine(localAppData, "Packages", packageFamilyName);
        var dataFolder = arguments.AppDataFolder ?? Path.Combine(packageFolder, "LocalState");
        var tempFolder = Path.Combine(packageFolder, "TempState");
        if (!Directory.Exists(tempFolder))
            tempFolder = Path.Combine(Path.GetTempPath(), "WinoIntelligenceConsole");

        Directory.CreateDirectory(tempFolder);
        return new ConsolePaths(dataFolder, Path.Combine(localAppData, PublisherRelativePath), tempFolder);
    }

    private static void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs eventArgs)
    {
        // Ctrl+C stops the running scenario and returns to the menu. At the menu it exits.
        if (_scenarioCancellation is { IsCancellationRequested: false } cancellation)
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
            ConsoleOutput.Warning("\nCancelling the current scenario...");
        }
    }
}

internal sealed record ConsoleArguments(
    ApiTarget? ApiTarget,
    string? AccountAddress,
    string? ScenarioKey,
    string? AppDataFolder,
    string? PackageFamilyName,
    bool AssumeYes,
    bool ShowHelp,
    string? LogFile = null)
{
    public static bool TryParse(string[] args, out ConsoleArguments arguments, out string? error)
    {
        ApiTarget? api = null;
        string? account = null, scenario = null, folder = null, package = null, log = null;
        bool yes = false, help = false;
        error = null;

        for (var index = 0; index < args.Length; index++)
        {
            var name = args[index];
            string? Value() => index + 1 < args.Length ? args[++index] : null;

            switch (name)
            {
                case "--api":
                    api = Value()?.ToLowerInvariant() switch
                    {
                        "local" => Hosting.ApiTarget.Local,
                        "prod" or "production" => Hosting.ApiTarget.Production,
                        _ => null,
                    };
                    if (api is null) error = "--api takes local or prod.";
                    break;
                case "--account": account = Value(); break;
                case "--scenario": scenario = Value(); break;
                case "--app-data-folder": folder = Value(); break;
                case "--package": package = Value(); break;
                case "--log": log = Value(); break;
                case "--yes" or "-y": yes = true; break;
                case "--help" or "-h" or "/?": help = true; break;
                default: error = $"Unknown argument '{name}'."; break;
            }

            if (error is not null)
                break;
        }

        arguments = new ConsoleArguments(api, account, scenario, folder, package, yes, help, log);
        return error is null;
    }

    public static void PrintHelp(IReadOnlyList<Scenario> scenarios)
    {
        Console.WriteLine("""
            Wino Intelligence console: the app's synchronization and intelligence services on the real database.
            Close Wino Mail first; the console refuses to run next to it.

              --api local|prod          API target (asked when omitted)
              --account <address>       mail account (asked when omitted)
              --scenario <key>          run one scenario and exit
              --package <family name>   package whose LocalState to use (default: Wino Mail Preview)
              --app-data-folder <path>  explicit LocalState folder
              --yes                     answer yes to every confirmation
              --log <file>              write the services' Serilog output (timings) to a file

            Scenarios:
            """);
        foreach (var scenario in scenarios)
            Console.WriteLine($"  {scenario.Key,-14} {scenario.Description}");
    }
}
