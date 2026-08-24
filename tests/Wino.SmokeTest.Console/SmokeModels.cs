using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.SmokeTest.ConsoleApp;

internal enum SmokeStepStatus
{
    Passed,
    Failed,
    Skipped
}

internal sealed record SmokeStepResult(
    string Name,
    SmokeStepStatus Status,
    TimeSpan Duration,
    string Details,
    IReadOnlyDictionary<string, int>? Counts = null);

internal sealed class SmokeRunResult
{
    public int SchemaVersion { get; init; } = 1;
    public required string RunId { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset FinishedAtUtc { get; set; }
    public required Guid AccountId { get; init; }
    public required string AccountAddress { get; init; }
    public required string Provider { get; init; }
    public List<SmokeStepResult> Steps { get; } = [];
    public bool FixtureCleanupSucceeded { get; set; }
    public bool ReportSent { get; set; }
    public string? ReportSendError { get; set; }

    [JsonIgnore]
    public bool HasFailures => Steps.Any(step => step.Status == SmokeStepStatus.Failed) ||
                               !FixtureCleanupSucceeded ||
                               !ReportSent;
}

internal static class SmokeReportBuilder
{
    public static string BuildText(SmokeRunResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Wino live-account smoke test");
        builder.AppendLine($"Run: {result.RunId}");
        builder.AppendLine($"Account: {result.AccountAddress}");
        builder.AppendLine($"Provider: {result.Provider}");
        builder.AppendLine($"Started: {result.StartedAtUtc:O}");
        builder.AppendLine();

        foreach (var step in result.Steps)
        {
            builder.AppendLine($"[{step.Status}] {step.Name} ({step.Duration.TotalSeconds:0.##} s)");
            if (!string.IsNullOrWhiteSpace(step.Details))
                builder.AppendLine($"  {step.Details}");
            if (step.Counts is not null)
                builder.AppendLine($"  {string.Join(", ", step.Counts.Select(pair => $"{pair.Key}: {pair.Value}"))}");
        }

        builder.AppendLine();
        builder.AppendLine($"Fixture cleanup: {(result.FixtureCleanupSucceeded ? "passed" : "failed")}");
        builder.AppendLine("The delivery of this report is the final smoke action and is recorded in the local result.json file.");
        return builder.ToString();
    }

    public static string BuildHtml(SmokeRunResult result)
    {
        static string Encode(string value) => WebUtility.HtmlEncode(value);

        var rows = string.Join(string.Empty, result.Steps.Select(step =>
            $"<tr><td>{Encode(step.Status.ToString())}</td><td>{Encode(step.Name)}</td>" +
            $"<td>{step.Duration.TotalSeconds:0.##} s</td><td>{Encode(step.Details)}</td></tr>"));

        return $"""
            <html><body>
            <h2>Wino live-account smoke test</h2>
            <p><b>Run:</b> {Encode(result.RunId)}<br>
            <b>Account:</b> {Encode(result.AccountAddress)}<br>
            <b>Provider:</b> {Encode(result.Provider)}<br>
            <b>Started:</b> {Encode(result.StartedAtUtc.ToString("O"))}</p>
            <table border="1" cellspacing="0" cellpadding="6">
            <thead><tr><th>Status</th><th>Step</th><th>Duration</th><th>Details</th></tr></thead>
            <tbody>{rows}</tbody></table>
            <p><b>Fixture cleanup:</b> {(result.FixtureCleanupSucceeded ? "passed" : "failed")}</p>
            <p>The delivery of this report is recorded in the local result.json file.</p>
            </body></html>
            """;
    }
}

internal sealed class SmokeRunArtifacts : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly StreamWriter _runLog;
    private readonly TextWriter _originalOut;
    private readonly TextWriter _originalError;

    private SmokeRunArtifacts(
        string folderPath,
        StreamWriter runLog,
        TextWriter originalOut,
        TextWriter originalError)
    {
        FolderPath = folderPath;
        _runLog = runLog;
        _originalOut = originalOut;
        _originalError = originalError;
    }

    public string FolderPath { get; }
    public string EngineLogPath => Path.Combine(FolderPath, "engine.log");

    public static SmokeRunArtifacts Create(string runId, string accountAddress)
    {
        var safeAccount = SanitizePathPart(accountAddress);
        var folder = Path.GetFullPath(Path.Combine(
            Environment.CurrentDirectory,
            "artifacts",
            "smoke-tests",
            $"{runId}-{safeAccount}"));
        Directory.CreateDirectory(folder);
        var writer = new StreamWriter(Path.Combine(folder, "run.log"), append: false, Encoding.UTF8)
        {
            AutoFlush = true
        };
        var originalOut = System.Console.Out;
        var originalError = System.Console.Error;
        var synchronizedLog = TextWriter.Synchronized(writer);
        System.Console.SetOut(new SmokeTeeTextWriter(originalOut, synchronizedLog));
        System.Console.SetError(new SmokeTeeTextWriter(originalError, synchronizedLog));
        return new SmokeRunArtifacts(folder, writer, originalOut, originalError);
    }

    public async Task WriteResultAsync(SmokeRunResult result, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(result, JsonOptions);
        await File.WriteAllTextAsync(Path.Combine(FolderPath, "result.json"), json, cancellationToken)
            .ConfigureAwait(false);
    }

    public void Dispose()
    {
        System.Console.SetOut(_originalOut);
        System.Console.SetError(_originalError);
        _runLog.Dispose();
    }

    internal static string SanitizePathPart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(character => invalid.Contains(character) ? '_' : character));
    }

    public void CompleteEngineLog()
    {
        try
        {
            Serilog.Log.CloseAndFlush();
            if (File.Exists(EngineLogPath))
                return;

            var rolledLog = Directory.GetFiles(FolderPath, "engine*.log")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (rolledLog is not null)
                File.Copy(rolledLog, EngineLogPath, overwrite: true);
        }
        catch (IOException exception)
        {
            ConsoleOutput.Warning($"Could not normalize engine.log: {exception.Message}");
        }
    }
}

internal sealed class SmokeTeeTextWriter(TextWriter primary, TextWriter log) : TextWriter
{
    private readonly object _writeLock = new();

    public override Encoding Encoding => primary.Encoding;

    public override void Write(string? value)
    {
        lock (_writeLock)
        {
            primary.Write(value);
            log.Write($"{DateTimeOffset.UtcNow:O} {value}");
        }
    }

    public override void Write(char value)
    {
        lock (_writeLock)
        {
            primary.Write(value);
            log.Write(value);
        }
    }

    public override void WriteLine(string? value)
    {
        lock (_writeLock)
        {
            primary.WriteLine(value);
            log.WriteLine($"{DateTimeOffset.UtcNow:O} {value}");
        }
    }

    public override void Flush()
    {
        lock (_writeLock)
        {
            primary.Flush();
            log.Flush();
        }
    }
}

internal sealed class SmokeProcessGuard : IDisposable
{
    internal const string MailHostMutexName = "Local\\WinoMailMailHostRunning";
    internal const string SmokeMutexName = "Local\\WinoSmokeTestConsoleRunning";
    private readonly Mutex _smokeMutex;

    private SmokeProcessGuard(Mutex smokeMutex)
    {
        _smokeMutex = smokeMutex;
    }

    public static bool TryAcquire(out SmokeProcessGuard? guard, out string? error)
    {
        guard = null;
        error = null;

        try
        {
            if (Mutex.TryOpenExisting(MailHostMutexName, out var mailHostMutex))
            {
                mailHostMutex.Dispose();
                error = "Wino Mail is running. Close it before using the smoke-test console.";
                return false;
            }
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }

        var smokeMutex = new Mutex(initiallyOwned: true, SmokeMutexName, out var createdNew);
        if (!createdNew)
        {
            smokeMutex.Dispose();
            error = "Another Wino smoke-test console is already running.";
            return false;
        }

        guard = new SmokeProcessGuard(smokeMutex);
        return true;
    }

    public void Dispose()
    {
        try
        {
            _smokeMutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }

        _smokeMutex.Dispose();
    }
}
