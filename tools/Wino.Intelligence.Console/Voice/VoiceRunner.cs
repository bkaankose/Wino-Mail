using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Voice;
using Wino.Intelligence.ConsoleApp.Hosting;
using Wino.Mail.Api.Contracts.Voice;
using Wino.Services;

namespace Wino.Intelligence.ConsoleApp.Voice;

/// <summary>
/// Runs voice prompts as text through the app's voice pipeline: the interpret socket (a text frame instead of audio),
/// the local questions of Private mode, the pickers with scripted answers, the preview, the resolution against the
/// local mailbox and the plan executor. The executor's requests and windows are recorded, never run. Works on a copy
/// of the app's databases, signed in to a Wino account of the API it targets.
/// </summary>
internal static class VoiceRunner
{
    private const string DefaultPackageFamilyName = "58272BurakKSE.WinoMailPreview_mhdqskaa8n2sj";
    private const string PublisherRelativePath = @"Publishers\mhdqskaa8n2sj\WinoShared";

    private static readonly object OutputGate = new();

    private static readonly JsonSerializerOptions LogJson = new() { WriteIndented = false };

    public static async Task<int> RunAsync(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (!VoiceRunnerArguments.TryParse(args, out var options, out var error))
        {
            Console.Error.WriteLine(error);
            VoiceRunnerArguments.PrintHelp();
            return 2;
        }

        if (options.ShowHelp)
        {
            VoiceRunnerArguments.PrintHelp();
            return 0;
        }

        var cases = options.Texts.Count > 0
            ? options.Texts.Select((text, index) => new VoiceCase { Id = $"text-{index + 1}", Domain = "adhoc", Text = text }).ToList()
            : VoiceCaseReader.Read(ExpandCorpus(options.Corpus));
        cases = cases.Where(item => string.IsNullOrWhiteSpace(item.Mode) || string.Equals(item.Mode, options.Mode.ToString(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (options.Filter is { } filter)
            cases = cases.Where(item => item.Id.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || item.Domain.Equals(filter, StringComparison.OrdinalIgnoreCase)
                || item.Text.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        if (options.Skip > 0)
            cases = cases.Skip(options.Skip).ToList();
        if (options.Limit > 0)
            cases = cases.Take(options.Limit).ToList();
        if (cases.Count == 0)
        {
            Console.Error.WriteLine("No prompts to run.");
            return 2;
        }

        var runFolder = options.OutputFolder ?? Path.Combine(Environment.CurrentDirectory, "voice-runs", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(runFolder);
        var dataFolder = Path.Combine(runFolder, "data");
        CopyAppData(options, dataFolder);

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var tempFolder = Path.Combine(runFolder, "temp");
        Directory.CreateDirectory(tempFolder);
        var paths = new ConsolePaths(dataFolder, Path.Combine(localAppData, PublisherRelativePath), tempFolder);

        ConsoleServices.ApiUrlOverride = new Uri(options.ApiUrl);
        await using var services = ConsoleServices.Create(paths, ApiTarget.Local, collection =>
        {
            collection.AddSingleton<IWinoRequestDelegator, RecordingRequestDelegator>();
            collection.AddSingleton<IClipboardService, RecordingClipboardService>();
            collection.AddSingleton<IExternalLauncher, RecordingExternalLauncher>();
            collection.AddSingleton<IUnsubscriptionService, RecordingUnsubscriptionService>();
            ReadOnlyServiceProxy<ICalendarService>.Decorate(collection);
            ReadOnlyServiceProxy<IMailService>.Decorate(collection, nameof(IMailService.ChangePinnedStatusAsync));
            ReadOnlyServiceProxy<IMailCategoryService>.Decorate(collection, nameof(IMailCategoryService.AssignCategoryAsync), nameof(IMailCategoryService.UnassignCategoryAsync));
            ReadOnlyServiceProxy<ISynchronizationManager>.Decorate(collection, nameof(ISynchronizationManager.UndoLatestQueuedAction));
            ReadOnlyServiceProxy<IWinoIntelligenceCoordinator>.Decorate(collection, nameof(IWinoIntelligenceCoordinator.RequestProcessingAsync));
        });
        RecordingMessageListener.Instance.Register();

        await services.GetRequiredService<IDatabaseService>().InitializeAsync();
        await services.GetRequiredService<IMailIntelligenceStore>().InitializeAsync();
        await services.GetRequiredService<ITranslationService>().InitializeAsync();

        var now = options.Now ?? DateTime.Now;
        now = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Unspecified);

        var seeded = options.Seed
            ? await VoiceFixtures.SeedAsync(services.GetRequiredService<ICalendarService>(), services.GetRequiredService<IAccountService>(), now)
            : 0;
        var seededItems = options.Seed
            ? await VoiceFixtures.SeedItemsAsync(services.GetRequiredService<ITaskService>(), services.GetRequiredService<IContactService>(), services.GetRequiredService<IAccountService>(), now)
            : 0;
        ReadOnlyServiceProxy<ICalendarService>.IsReadOnly = true;
        ReadOnlyServiceProxy<IMailService>.IsReadOnly = true;
        ReadOnlyServiceProxy<IMailCategoryService>.IsReadOnly = true;
        ReadOnlyServiceProxy<ISynchronizationManager>.IsReadOnly = true;
        ReadOnlyServiceProxy<IWinoIntelligenceCoordinator>.IsReadOnly = true;

        var textLog = new StreamWriter(Path.Combine(runFolder, "run.log"), append: false, Encoding.UTF8) { AutoFlush = true };
        var jsonLog = new StreamWriter(Path.Combine(runFolder, "run.jsonl"), append: false, Encoding.UTF8) { AutoFlush = true };
        try
        {
            var accounts = await services.GetRequiredService<IAccountService>().GetAccountsAsync() ?? [];
            var header = new StringBuilder();
            header.AppendLine($"Wino voice runner  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            header.AppendLine($"API {options.ApiUrl}  mode {options.Mode}  clock {now:ddd yyyy-MM-dd HH:mm} ({TimeZoneInfo.Local.Id})  languages {string.Join(",", options.Languages)}");
            header.AppendLine($"Data copy {dataFolder}");
            header.AppendLine($"Accounts: {string.Join("; ", accounts.Select(static account => $"{account.Name} <{account.Address}> {account.ProviderType}"))}");
            header.AppendLine($"{cases.Count} prompt(s), {options.Parallel} at a time{(options.Learn ? ", answers saved as facts" : string.Empty)}{(seeded > 0 ? $", {seeded} fixture events and {seededItems} tasks and contacts in the copy" : string.Empty)}");
            Emit(textLog, header.ToString(), ConsoleColor.Cyan);

            if (!await SignInAsync(services, options, textLog))
                return 2;

            if (!await PrepareModeAsync(services, options, accounts, textLog))
                return 2;

            var results = new VoiceCaseResult[cases.Count];
            using var gate = new SemaphoreSlim(options.Parallel);
            var clock = Stopwatch.StartNew();
            var finished = 0;
            var tasks = cases.Select(async (item, index) =>
            {
                await gate.WaitAsync();
                try
                {
                    var result = await RunCaseAsync(services, options, item, now);
                    results[index] = result;
                    var number = Interlocked.Increment(ref finished);
                    lock (OutputGate)
                    {
                        WriteCase(textLog, result, number, cases.Count, options.Quiet);
                        jsonLog.WriteLine(result.ToJson());
                    }
                }
                finally
                {
                    gate.Release();
                }
            });
            await Task.WhenAll(tasks);

            var summary = Summarize(results, clock.Elapsed);
            Emit(textLog, summary, ConsoleColor.Cyan);
            await File.WriteAllTextAsync(Path.Combine(runFolder, "summary.txt"), summary);
            Emit(textLog, $"Logs: {runFolder}", ConsoleColor.DarkGray);
            return results.All(static result => result.Passed != false && result.Error is null) ? 0 : 1;
        }
        finally
        {
            await textLog.DisposeAsync();
            await jsonLog.DisposeAsync();
            await Serilog.Log.CloseAndFlushAsync();
        }
    }

    private static IEnumerable<string> ExpandCorpus(IEnumerable<string> entries)
    {
        foreach (var entry in entries)
        {
            if (Directory.Exists(entry))
            {
                foreach (var file in Directory.GetFiles(entry, "*.jsonl", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
                    yield return file;
            }
            else
            {
                yield return entry;
            }
        }
    }

    /// <summary>The app keeps its databases open; SQLite's WAL files are copied with them so the copy is consistent.</summary>
    private static void CopyAppData(VoiceRunnerArguments options, string dataFolder)
    {
        var source = options.AppDataFolder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", options.PackageFamilyName ?? DefaultPackageFamilyName, "LocalState");
        Directory.CreateDirectory(dataFolder);
        foreach (var name in new[] { "Wino210.db", "WinoMailIntelligence.db" })
        {
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                var from = Path.Combine(source, name + suffix);
                if (!File.Exists(from))
                    continue;

                using var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var output = File.Create(Path.Combine(dataFolder, name + suffix));
                input.CopyTo(output);
            }
        }

        if (!File.Exists(Path.Combine(dataFolder, "Wino210.db")))
            throw new InvalidOperationException($"Wino210.db was not found in {source}.");
    }

    private static async Task<bool> SignInAsync(IServiceProvider services, VoiceRunnerArguments options, TextWriter log)
    {
        var database = services.GetRequiredService<IDatabaseService>();
        await database.Connection.DeleteAllAsync<WinoAccount>();

        var login = JsonNode.Parse(await File.ReadAllTextAsync(options.LoginFile!))!;
        var email = login["Email"]?.GetValue<string>() ?? login["email"]?.GetValue<string>();
        var password = login["Password"]?.GetValue<string>() ?? login["password"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            Emit(log, "The login file needs Email and Password.", ConsoleColor.Red);
            return false;
        }

        var result = await services.GetRequiredService<IWinoAccountProfileService>().LoginAsync(email, password);
        if (!result.IsSuccess)
        {
            Emit(log, $"Sign-in to {options.ApiUrl} failed: {result.ErrorCode}", ConsoleColor.Red);
            return false;
        }

        Emit(log, $"Signed in as {email}", ConsoleColor.DarkGray);
        return true;
    }

    /// <summary>
    /// Private: the server brain is deleted. Personal: the brain is rebuilt from this data copy (the same push the app
    /// does), then facts from <see cref="VoiceRunnerArguments.FactsFile"/> are added.
    /// </summary>
    private static async Task<bool> PrepareModeAsync(IServiceProvider services, VoiceRunnerArguments options, IReadOnlyList<MailAccount> accounts, TextWriter log)
    {
        var brain = services.GetRequiredService<IVoiceBrainService>();
        await brain.StartAsync();
        if (!await brain.SetModeAsync(VoiceMode.Private))
        {
            Emit(log, "Could not switch the Wino account to Private mode (AI Pack and consent?).", ConsoleColor.Red);
            return false;
        }

        if (options.Mode == VoiceMode.Private)
            return true;

        if (!await brain.SetModeAsync(VoiceMode.Personal))
        {
            Emit(log, "Could not switch the Wino account to Personal mode.", ConsoleColor.Red);
            return false;
        }

        var clock = Stopwatch.StartNew();
        var expected = accounts.Count;
        VoiceBrainSnapshot? snapshot = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(90))
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            snapshot = await brain.GetBrainAsync();
            if (snapshot?.Brain.Sections.Count >= expected)
                break;
        }

        var sections = snapshot?.Brain.Sections ?? [];
        Emit(log, $"Brain: {sections.Count}/{expected} sections pushed in {clock.Elapsed.TotalSeconds:0.0} s", sections.Count >= expected ? ConsoleColor.DarkGray : ConsoleColor.Yellow);
        foreach (var section in sections)
        {
            var s = section.Section;
            Emit(log, $"  {s.Mailbox.Address} ({s.Mailbox.Provider}): {s.Folders.Count} folders, {s.Calendars.Count} calendars, {s.TaskLists.Count} lists, {s.TopContacts.Count} contacts, name \"{s.AccountName}\" order {s.Order}", ConsoleColor.DarkGray);
        }

        if (options.FactsFile is { } factsFile)
        {
            var facts = JsonSerializer.Deserialize<List<AddBrainFactRequest>>(await File.ReadAllTextAsync(factsFile), VoiceCaseReader.Options) ?? [];
            foreach (var fact in facts)
                await brain.AddFactAsync(fact);
            Emit(log, $"Added {facts.Count} fact(s) from {factsFile}", ConsoleColor.DarkGray);
        }

        return true;
    }

    private static async Task<VoiceCaseResult> RunCaseAsync(IServiceProvider services, VoiceRunnerArguments options, VoiceCase item, DateTime now)
    {
        var result = new VoiceCaseResult(item, now);
        var clock = Stopwatch.StartNew();
        try
        {
            var (plan, metrics, serverError) = await InterpretAsync(services, options, item.Text, now);
            result.InterpretMs = (int)clock.ElapsedMilliseconds;
            if (plan is null)
            {
                result.Error = serverError;
                return result;
            }

            result.Metrics = metrics;
            result.RawPlan = JsonSerializer.SerializeToNode(plan, VoiceJsonContext.Default.VoicePlan);
            var mode = metrics?.Mode ?? options.Mode;

            if (VoicePlan.IsCanceledByUser(plan) || (plan.Commands?.Count ?? 0) == 0)
            {
                result.Outcome = "Closed (cancelled by the user or no commands)";
                result.FinalPlan = result.RawPlan;
                return result;
            }

            var questionService = services.GetRequiredService<IVoiceQuestionService>();
            plan = await questionService.AddLocalQuestionsAsync(plan);
            if (mode == VoiceMode.Personal)
            {
                var known = await questionService.AnswerKnownQuestionsAsync(plan);
                plan = known.Plan;
                result.AutoAnswered = known.Facts.Select(static fact => $"{fact.Kind} \"{fact.Label}\" -> {fact.TargetName ?? fact.Mailbox?.Address}").ToList();
                if (options.Learn)
                {
                    foreach (var fact in known.Facts)
                        await services.GetRequiredService<IVoiceBrainService>().AddFactAsync(fact);
                }
            }

            var answers = new Queue<string>(item.Answers ?? []);
            foreach (var question in questionService.GetQuestions(plan))
            {
                var asked = new AskedQuestion(question.Kind.ToString(), question.Phrase, question.Text);
                result.Questions.Add(asked);

                var candidates = await questionService.GetCandidatesAsync(question, null);
                asked.Candidates = candidates.Take(8).Select(DescribeCandidate).ToList();
                asked.CandidateCount = candidates.Count;

                var wanted = answers.Count > 0 ? answers.Dequeue() : null;
                VoiceAnswerCandidate? answer = null;
                if (question.AllowsMany)
                {
                    // A group answer names its people with "+" ("Jay+Christian"); without one, the first two candidates.
                    var members = new List<VoiceAnswerCandidate>();
                    foreach (var name in wanted?.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [])
                    {
                        var member = candidates.FirstOrDefault(candidate => Matches(candidate, name))
                            ?? (await questionService.GetCandidatesAsync(question, name)).FirstOrDefault();
                        if (member is not null && !members.Exists(m => m.Key == member.Key))
                            members.Add(member);
                    }

                    if (wanted is null)
                        members.AddRange(candidates.Take(2));

                    answer = members.Count > 0 ? VoiceAnswerCandidate.Group(members) : null;
                }
                else if (wanted is null)
                {
                    answer = candidates.FirstOrDefault();
                }
                else
                {
                    answer = candidates.FirstOrDefault(candidate => Matches(candidate, wanted));
                    if (answer is null)
                    {
                        var filtered = await questionService.GetCandidatesAsync(question, wanted);
                        answer = filtered.FirstOrDefault(candidate => Matches(candidate, wanted)) ?? filtered.FirstOrDefault();
                        asked.Filter = wanted;
                    }
                }

                if (answer is null)
                {
                    asked.Answer = "(nothing to pick: the user closes the bubble)";
                    result.Outcome = "Closed at a question with no candidates";
                    result.FinalPlan = JsonSerializer.SerializeToNode(plan, VoiceJsonContext.Default.VoicePlan);
                    return result;
                }

                asked.Answer = DescribeCandidate(answer);
                plan = questionService.ApplyAnswer(plan, question, answer);

                if (mode == VoiceMode.Personal && options.Learn && questionService.CreateFact(question, answer) is { } fact)
                {
                    await services.GetRequiredService<IVoiceBrainService>().AddFactAsync(fact);
                    asked.Learned = true;
                }
            }

            result.FinalPlan = JsonSerializer.SerializeToNode(plan, VoiceJsonContext.Default.VoicePlan);

            var executor = services.GetRequiredService<IVoicePlanExecutor>();
            var preview = await services.GetRequiredService<IVoicePlanPreviewService>().CreateAsync(plan);
            var resolution = await executor.ResolveAsync(preview);
            result.Groups = resolution.Preview.Groups.Select(DescribeGroup).ToList();

            var accepted = resolution.Preview.Groups.Where(static group => group.Steps.Count > 0).ToList();
            if (accepted.Count == 0)
            {
                var issue = resolution.Preview.Groups.SelectMany(static group => group.Issues).FirstOrDefault();
                result.Outcome = $"Nothing to run: {issue?.Message ?? "not understood"}";
                return result;
            }

            result.Risks = executor.GetRisks(resolution, accepted).Select(static risk => $"{Plain(risk.MarkedText)} [{risk.ButtonText}]").ToList();

            var operations = new List<RecordedOperation>();
            OperationLog.Current = operations;
            try
            {
                var execution = await executor.ExecuteAsync(resolution, accepted, plan.Confidence, new RecordingPlanHost());
                result.Completed = execution.Completed.Select(Plain).ToList();
                result.Failed = execution.Failed.Select(Plain).ToList();
                result.Summary = execution.Summary;
            }
            finally
            {
                OperationLog.Current = null;
            }

            result.Operations = operations;
            result.Outcome = result.Failed.Count > 0 ? "Ran with failures" : "Ran";
        }
        catch (Exception exception)
        {
            result.Error = $"{exception.GetType().Name}: {exception.Message}";
        }
        finally
        {
            result.TotalMs = (int)clock.ElapsedMilliseconds;
            var expected = item.ExpectFor(result.Metrics?.Mode ?? options.Mode);
            if (expected is { } expect && result.FinalPlan is { } finalPlan)
            {
                var groupAccounts = result.Groups.Where(static group => group.Steps.Count > 0 || group.Issues.Count > 0).Select(static group => group.Account).ToList();
                result.Checks = VoiceCaseChecker.Evaluate(expect, finalPlan, result.Questions.Select(static q => q.Kind).ToList(), result.Operations, result.Outcome, groupAccounts, now);
                result.Passed = result.Checks.All(static check => check.Passed);
            }
            else if (expected is not null)
            {
                result.Passed = false;
            }
        }

        return result;
    }

    /// <summary>One interpret request over the app's socket: start with the pinned clock, then the prompt as a text frame.</summary>
    private static async Task<(VoicePlan? Plan, CommandMetrics? Metrics, string? Error)> InterpretAsync(IServiceProvider services, VoiceRunnerArguments options, string text, DateTime now)
    {
        var api = services.GetRequiredService<IWinoAccountApiClient>();
        for (var attempt = 1; ; attempt++)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var connection = await api.ConnectVoiceAsync(timeout.Token);
            if (connection.Socket is not { } socket)
            {
                if (attempt < 4)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
                    continue;
                }

                return (null, null, $"connect failed: {connection.ErrorCode}");
            }

            using (socket)
            {
                await SendAsync(socket, new VoiceClientFrame(VoiceFrameTypes.Start, new VoiceInterpretContext(TimeZoneInfo.Local.Id, now, options.Languages.ToList(), VoiceInputKind.Text, WorkDayStart: new TimeOnly(9, 0), WorkDayEnd: new TimeOnly(18, 0))), timeout.Token);
                var sentText = false;
                while (true)
                {
                    var frame = await ReceiveAsync(socket, timeout.Token);
                    if (frame is null)
                        return (null, null, "the socket closed without a plan");

                    switch (frame.Type)
                    {
                        case VoiceFrameTypes.Ready when !sentText:
                            sentText = true;
                            await SendAsync(socket, new VoiceClientFrame(VoiceFrameTypes.Text, Text: text), timeout.Token);
                            break;
                        case VoiceFrameTypes.Plan when frame.Plan is { } plan:
                            return (plan, frame.Metrics, null);
                        case VoiceFrameTypes.Error:
                            if ((frame.Code is VoiceErrorCodes.Superseded or VoiceErrorCodes.Timeout) && attempt < 3)
                                goto retry;
                            return (null, null, $"server error {frame.Code}: {frame.Message}");
                    }
                }
            }

        retry:
            await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
        }
    }

    private static async Task SendAsync(WebSocket socket, VoiceClientFrame frame, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, VoiceJsonContext.Default.VoiceClientFrame);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }

    private static async Task<VoiceServerFrame?> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult received;
            try
            {
                received = await socket.ReceiveAsync(buffer, cancellationToken);
            }
            catch (WebSocketException)
            {
                return null;
            }

            if (received.MessageType == WebSocketMessageType.Close)
                return null;

            message.Write(buffer, 0, received.Count);
            if (received.EndOfMessage)
                break;
        }

        return JsonSerializer.Deserialize(message.ToArray(), VoiceJsonContext.Default.VoiceServerFrame);
    }

    private static bool Matches(VoiceAnswerCandidate candidate, string wanted)
    {
        var folded = VoiceCaseChecker.Fold(wanted);
        return new[] { candidate.Title, candidate.Detail, candidate.Address, candidate.GroupTitle, candidate.Account?.Address }
            .Any(value => value is not null && VoiceCaseChecker.Fold(value).Contains(folded, StringComparison.Ordinal));
    }

    private static string DescribeCandidate(VoiceAnswerCandidate candidate)
    {
        var text = candidate.Title;
        if (!string.IsNullOrWhiteSpace(candidate.Detail))
            text += $" ({candidate.Detail})";
        if (!string.IsNullOrWhiteSpace(candidate.GroupTitle))
            text += $" [{candidate.GroupTitle}]";
        return text;
    }

    private static PreviewGroup DescribeGroup(VoicePlanPreviewGroup group) => new(
        group.Kind.ToString(),
        group.Account is { } account ? $"{account.Name} <{account.Address}>" : group.SpokenAccount,
        group.Candidates.Select(static candidate => candidate.Address).ToList(),
        group.Steps.Select(static step => step.Number + ". " + Plain(step.MarkedText) + (string.IsNullOrWhiteSpace(step.Detail) ? string.Empty : $"  ({step.Detail})")).ToList(),
        group.Issues.Select(static issue => issue.Message).ToList());

    private static string Plain(string text) => text.Replace("**", string.Empty);

    private static void WriteCase(TextWriter log, VoiceCaseResult result, int number, int total, bool quiet)
    {
        var text = result.Format(number, total);
        log.Write(text);
        if (quiet && result.Passed != false && result.Error is null)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"[{number}/{total}] {result.Case.Id} ok");
            Console.ResetColor();
            return;
        }

        foreach (var line in text.Split(Environment.NewLine))
        {
            Console.ForegroundColor = line switch
            {
                _ when line.StartsWith('[') => result.Error is not null || result.Passed == false ? ConsoleColor.Red : ConsoleColor.Green,
                _ when line.Contains("FAIL ", StringComparison.Ordinal) => ConsoleColor.Red,
                _ when line.TrimStart().StartsWith("Question", StringComparison.Ordinal) => ConsoleColor.Yellow,
                _ when line.TrimStart().StartsWith("Op ", StringComparison.Ordinal) => ConsoleColor.Cyan,
                _ => ConsoleColor.Gray,
            };
            Console.WriteLine(line);
        }

        Console.ResetColor();
    }

    private static void Emit(TextWriter log, string text, ConsoleColor color)
    {
        lock (OutputGate)
        {
            log.WriteLine(text);
            Console.ForegroundColor = color;
            Console.WriteLine(text);
            Console.ResetColor();
        }
    }

    private static string Summarize(IReadOnlyList<VoiceCaseResult> results, TimeSpan elapsed)
    {
        var builder = new StringBuilder();
        builder.AppendLine();
        builder.AppendLine($"=== {results.Count} prompt(s) in {elapsed.TotalMinutes:0.0} min ===");
        var checkedResults = results.Where(static result => result.Passed is not null).ToList();
        builder.AppendLine($"Errors: {results.Count(static result => result.Error is not null)}   Checked: {checkedResults.Count}   Passed: {checkedResults.Count(static result => result.Passed == true)}   Failed: {checkedResults.Count(static result => result.Passed == false)}");
        builder.AppendLine($"Median interpret {Median(results.Select(static result => result.InterpretMs))} ms, total {Median(results.Select(static result => result.TotalMs))} ms");
        builder.AppendLine();
        builder.AppendLine("Domain                 run   checked  passed   rate");
        foreach (var domain in results.GroupBy(static result => result.Case.Domain).OrderBy(static group => group.Key))
        {
            var checkedInDomain = domain.Count(static result => result.Passed is not null);
            var passed = domain.Count(static result => result.Passed == true);
            builder.AppendLine($"{domain.Key,-22} {domain.Count(),4}   {checkedInDomain,6}  {passed,6}   {(checkedInDomain == 0 ? "-" : $"{100.0 * passed / checkedInDomain:0.0}%"),6}");
        }

        var failures = results.Where(static result => result.Passed == false || result.Error is not null).ToList();
        if (failures.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Failures:");
            foreach (var failure in failures)
            {
                var reason = failure.Error ?? string.Join("; ", failure.Checks.Where(static check => !check.Passed).Select(static check => $"{check.Name} (got {check.Actual})"));
                builder.AppendLine($"  {failure.Case.Id}  \"{failure.Case.Text}\"  -> {reason}");
            }
        }

        return builder.ToString();
    }

    private static int Median(IEnumerable<int> values)
    {
        var sorted = values.Where(static value => value > 0).Order().ToList();
        return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    }
}

internal sealed class AskedQuestion(string kind, string phrase, string text)
{
    public string Kind { get; } = kind;
    public string Phrase { get; } = phrase;
    public string Text { get; } = text;
    public int CandidateCount { get; set; }
    public List<string> Candidates { get; set; } = [];
    public string? Filter { get; set; }
    public string? Answer { get; set; }
    public bool Learned { get; set; }
}

internal sealed record PreviewGroup(string Kind, string? Account, List<string> Candidates, List<string> Steps, List<string> Issues);

internal sealed class VoiceCaseResult(VoiceCase item, DateTime now)
{
    public VoiceCase Case { get; } = item;
    public DateTime Now { get; } = now;
    public CommandMetrics? Metrics { get; set; }
    public JsonNode? RawPlan { get; set; }
    public JsonNode? FinalPlan { get; set; }
    public List<AskedQuestion> Questions { get; } = [];
    public List<string> AutoAnswered { get; set; } = [];
    public List<PreviewGroup> Groups { get; set; } = [];
    public List<string> Risks { get; set; } = [];
    public List<RecordedOperation> Operations { get; set; } = [];
    public List<string> Completed { get; set; } = [];
    public List<string> Failed { get; set; } = [];
    public string? Summary { get; set; }
    public string? Outcome { get; set; }
    public string? Error { get; set; }
    public List<CheckResult> Checks { get; set; } = [];
    public bool? Passed { get; set; }
    public int InterpretMs { get; set; }
    public int TotalMs { get; set; }

    public string Format(int number, int total)
    {
        var builder = new StringBuilder();
        var status = Error is not null ? "ERROR" : Passed switch { true => "PASS", false => "FAIL", _ => "RAN" };
        builder.AppendLine($"[{number}/{total}] {status}  {Case.Id} ({Case.Domain})");
        builder.AppendLine($"  Prompt     \"{Case.Text}\"");
        if (Error is not null)
        {
            builder.AppendLine($"  Error      {Error}");
            builder.AppendLine();
            return builder.ToString();
        }

        if (RawPlan is { } plan)
        {
            builder.AppendLine($"  Plan       {Metrics?.Mode} {InterpretMs} ms, confidence {plan["confidence"]}, in {Metrics?.InputTokens} (cached {Metrics?.CacheReadTokens}) out {Metrics?.OutputTokens}: {plan["summary"]}");
            var index = 0;
            foreach (var command in plan["commands"]?.AsArray() ?? [])
            {
                index++;
                builder.AppendLine($"   {index}. {DescribeCommand(command)}");
            }
        }

        foreach (var line in AutoAnswered)
            builder.AppendLine($"  Answered   on the device: {line}");

        foreach (var question in Questions)
        {
            builder.AppendLine($"  Question   {question.Kind} \"{question.Phrase}\": {question.Text}  ({question.CandidateCount} candidates: {string.Join("; ", question.Candidates.Take(4))}{(question.CandidateCount > 4 ? "; ..." : string.Empty)})");
            builder.AppendLine($"    Answer   {(question.Filter is null ? string.Empty : $"typed \"{question.Filter}\" -> ")}{question.Answer}{(question.Learned ? "  (saved as a fact)" : string.Empty)}");
        }

        if (Questions.Count > 0 && FinalPlan is { } final)
        {
            var index = 0;
            foreach (var command in final["commands"]?.AsArray() ?? [])
            {
                index++;
                builder.AppendLine($"   {index}' {DescribeCommand(command)}");
            }
        }

        foreach (var group in Groups)
        {
            builder.AppendLine($"  Preview    {group.Kind}{(group.Account is null ? string.Empty : $" {group.Account}")}{(group.Candidates.Count > 0 ? $" candidates [{string.Join(", ", group.Candidates)}]" : string.Empty)}");
            foreach (var step in group.Steps)
                builder.AppendLine($"     {step}");
            foreach (var issue in group.Issues)
                builder.AppendLine($"     can't: {issue}");
        }

        foreach (var risk in Risks)
            builder.AppendLine($"  Risk       {risk}");
        foreach (var operation in Operations)
            builder.AppendLine($"  Op         {operation.Kind}: {operation.Description}");
        foreach (var line in Completed)
            builder.AppendLine($"  Done       {line}");
        foreach (var line in Failed)
            builder.AppendLine($"  Failed     {line}");
        if (!string.IsNullOrWhiteSpace(Summary))
            builder.AppendLine($"  Summary    {Describer.Clip(Summary, 200)}");
        builder.AppendLine($"  Outcome    {Outcome}");

        foreach (var check in Checks)
            builder.AppendLine($"  {(check.Passed ? "ok   " : "FAIL ")}      {check.Name}{(check.Passed ? string.Empty : $"  (got {check.Actual})")}");
        builder.AppendLine();
        return builder.ToString();
    }

    /// <summary>The command's non-empty fields on one line, without the envelope noise.</summary>
    private static string DescribeCommand(JsonNode? command)
    {
        if (command is not JsonObject obj)
            return "?";

        var parts = new List<string> { obj["action"]?.ToString() ?? "?" };
        foreach (var (key, value) in obj)
        {
            if (key is "action" or "summary" || value is null)
                continue;
            if (key == "mailbox" && value["scope"]?.ToString() is "Unresolved" or "Default" && value["account"] is null)
                continue;

            var text = Compact(value);
            if (text.Length > 0)
                parts.Add($"{key}={text}");
        }

        return string.Join("  ", parts);
    }

    private static string Compact(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            var inner = obj
                .Where(static pair => pair.Value is not null && !IsDefault(pair.Value))
                .Select(pair => $"{pair.Key}:{Compact(pair.Value!)}");
            return "{" + string.Join(", ", inner) + "}";
        }

        if (node is JsonArray array)
            return "[" + string.Join(", ", array.Where(static item => item is not null).Select(static item => Compact(item!))) + "]";

        return node.ToJsonString();
    }

    private static bool IsDefault(JsonNode node) => node switch
    {
        JsonArray array => array.Count == 0,
        JsonValue value when value.TryGetValue<string>(out var text) => text.Length == 0 || text is "Unspecified" or "Any" or "None",
        JsonValue value when value.TryGetValue<bool>(out var flag) => !flag,
        JsonValue value when value.TryGetValue<int>(out var number) => number == 0,
        _ => false,
    };

    public string ToJson()
    {
        var node = new JsonObject
        {
            ["id"] = Case.Id,
            ["domain"] = Case.Domain,
            ["text"] = Case.Text,
            ["now"] = Now.ToString("yyyy-MM-ddTHH:mm"),
            ["mode"] = Metrics?.Mode?.ToString(),
            ["interpretMs"] = InterpretMs,
            ["totalMs"] = TotalMs,
            ["inputTokens"] = Metrics?.InputTokens,
            ["outputTokens"] = Metrics?.OutputTokens,
            ["cacheReadTokens"] = Metrics?.CacheReadTokens,
            ["error"] = Error,
            ["outcome"] = Outcome,
            ["passed"] = Passed,
            ["rawPlan"] = RawPlan?.DeepClone(),
            ["finalPlan"] = Questions.Count > 0 ? FinalPlan?.DeepClone() : null,
            ["autoAnswered"] = JsonSerializer.SerializeToNode(AutoAnswered),
            ["questions"] = JsonSerializer.SerializeToNode(Questions.Select(static q => new { q.Kind, q.Phrase, q.Text, q.CandidateCount, q.Candidates, q.Filter, q.Answer, q.Learned }).ToList()),
            ["preview"] = JsonSerializer.SerializeToNode(Groups),
            ["risks"] = JsonSerializer.SerializeToNode(Risks),
            ["operations"] = JsonSerializer.SerializeToNode(Operations),
            ["completed"] = JsonSerializer.SerializeToNode(Completed),
            ["failed"] = JsonSerializer.SerializeToNode(Failed),
            ["summary"] = Summary,
            ["checks"] = JsonSerializer.SerializeToNode(Checks),
            ["note"] = Case.Note,
        };
        return node.ToJsonString();
    }
}

internal sealed class VoiceRunnerArguments
{
    public List<string> Corpus { get; } = [];
    public List<string> Texts { get; } = [];
    public VoiceMode Mode { get; set; } = VoiceMode.Private;
    public string ApiUrl { get; set; } = "http://localhost:5310/";
    public string? LoginFile { get; set; }
    public string? OutputFolder { get; set; }
    public string? AppDataFolder { get; set; }
    public string? PackageFamilyName { get; set; }
    public string? FactsFile { get; set; }
    public string? Filter { get; set; }
    public int Parallel { get; set; } = 4;
    public int Limit { get; set; }
    public int Skip { get; set; }
    public DateTime? Now { get; set; }
    public string[] Languages { get; set; } = ["en"];
    public bool Learn { get; set; }
    public bool Seed { get; set; }
    public bool Quiet { get; set; }
    public bool ShowHelp { get; set; }

    public static bool TryParse(string[] args, out VoiceRunnerArguments options, out string? error)
    {
        options = new VoiceRunnerArguments();
        error = null;
        for (var index = 0; index < args.Length; index++)
        {
            var name = args[index];
            string Value() => index + 1 < args.Length ? args[++index] : throw new ArgumentException($"{name} needs a value.");
            try
            {
                switch (name)
                {
                    case "--corpus": options.Corpus.Add(Value()); break;
                    case "--text": options.Texts.Add(Value()); break;
                    case "--mode": options.Mode = Enum.Parse<VoiceMode>(Value(), ignoreCase: true); break;
                    case "--api-url": options.ApiUrl = Value(); break;
                    case "--login": options.LoginFile = Value(); break;
                    case "--out": options.OutputFolder = Value(); break;
                    case "--app-data-folder": options.AppDataFolder = Value(); break;
                    case "--package": options.PackageFamilyName = Value(); break;
                    case "--facts": options.FactsFile = Value(); break;
                    case "--filter": options.Filter = Value(); break;
                    case "--parallel": options.Parallel = Math.Clamp(int.Parse(Value()), 1, 32); break;
                    case "--limit": options.Limit = int.Parse(Value()); break;
                    case "--skip": options.Skip = int.Parse(Value()); break;
                    case "--now": options.Now = DateTime.Parse(Value(), System.Globalization.CultureInfo.InvariantCulture); break;
                    case "--languages": options.Languages = Value().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); break;
                    case "--learn": options.Learn = true; break;
                    case "--seed": options.Seed = true; break;
                    case "--quiet": options.Quiet = true; break;
                    case "--help" or "-h": options.ShowHelp = true; break;
                    default: error = $"Unknown argument '{name}'."; return false;
                }
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException)
            {
                error = exception.Message;
                return false;
            }
        }

        if (!options.ShowHelp && options.Corpus.Count == 0 && options.Texts.Count == 0)
            error = "Give --corpus <file or folder> or --text <prompt>.";
        else if (!options.ShowHelp && options.LoginFile is null)
            error = "Give --login <json file with Email and Password> for the API's Wino account.";
        return error is null;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
            Wino voice runner: voice prompts as text through the app's voice pipeline, nothing executed.
            The app may stay open: the runner works on a copy of its databases in the output folder.
            The API must accept text frames (Voice:Transcription:TextOnly on a local API).

              voice --corpus <file|folder>   JSON lines with id, domain, text, answers, expect (repeatable)
                    --text "<prompt>"         an ad-hoc prompt instead of a corpus (repeatable)
                    --login <file>            JSON with Email and Password of the API's Wino account
                    --mode private|personal   voice mode for the run (default private)
                    --api-url <url>           default http://localhost:5310/
                    --facts <file>            Personal: brain facts (AddBrainFactRequest JSON array) added first
                    --learn                   Personal: save picker answers as facts, like the app
                    --seed                    add fixture events (standups, reviews, invites) to the data copy
                    --parallel <n>            prompts at a time (default 4)
                    --filter <text>           id, domain or prompt text filter
                    --skip <n> --limit <n>    run a slice
                    --now <yyyy-MM-ddTHH:mm>  pin the clock the planner sees
                    --languages en,tr         languages sent with each prompt (default en)
                    --out <folder>            run folder (default voice-runs\<timestamp>)
                    --quiet                   print only failing prompts in full
            """);
    }
}
