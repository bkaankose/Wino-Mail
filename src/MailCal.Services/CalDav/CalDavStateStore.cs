#nullable enable
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Domain.Models.Calendar;

namespace MailCal.Services.CalDav;

/// <summary>
/// Stores <see cref="CalDavAccountState"/> at
/// <c>&lt;MimeStorageFolderPath&gt;/CalendarIcs/&lt;accountId:N&gt;/caldav-state.json</c>.
/// Writes go to a temporary file that replaces the old one, so a crash leaves either version.
/// The file is a cache: anything unreadable is logged and treated as no state.
/// </summary>
public sealed class CalDavStateStore : ICalDavStateStore
{
    public const string FileName = "caldav-state.json";

    private static readonly ILogger Logger = Log.ForContext<CalDavStateStore>();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileLocks = new(StringComparer.Ordinal);
    private readonly Func<string> _rootPathProvider;

    public CalDavStateStore(IApplicationConfiguration applicationConfiguration)
        : this(() => Path.Combine(applicationConfiguration.MimeStorageFolderPath, "CalendarIcs"))
    {
        ArgumentNullException.ThrowIfNull(applicationConfiguration);
    }

    /// <param name="rootPathProvider">Returns the CalendarIcs root folder.</param>
    internal CalDavStateStore(Func<string> rootPathProvider)
    {
        _rootPathProvider = rootPathProvider ?? throw new ArgumentNullException(nameof(rootPathProvider));
    }

    public string GetStatePath(Guid accountId)
        => Path.Combine(_rootPathProvider(), accountId.ToString("N"), FileName);

    public async Task<CalDavAccountState> GetAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var path = GetStatePath(accountId);
        var gate = FileLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadAsync(path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveAsync(Guid accountId, CalDavAccountState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        var path = GetStatePath(accountId);
        var gate = FileLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteAsync(path, state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task UpdateAsync(Guid accountId, Action<CalDavAccountState> update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        var path = GetStatePath(accountId);
        var gate = FileLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
            update(state);
            await WriteAsync(path, state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task DeleteAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var path = GetStatePath(accountId);
        var gate = FileLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException ex)
        {
            Logger.Warning(ex, "Could not delete the CalDAV state of account {AccountId}", accountId);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<CalDavAccountState> ReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return new CalDavAccountState();

        try
        {
            await using var stream = File.OpenRead(path);
            var state = await JsonSerializer.DeserializeAsync(stream, CalDavStateJsonContext.Default.CalDavAccountState, cancellationToken).ConfigureAwait(false);

            if (state == null || state.Version != CalDavAccountState.CurrentVersion)
                return new CalDavAccountState();

            // Collections deserialize as null when the file says so explicitly.
            state.CalendarHomeHrefs ??= [];
            state.CalendarUserAddresses ??= [];
            state.DavCapabilities ??= [];
            state.Calendars ??= [];
            foreach (var calendar in state.Calendars.Values)
            {
                calendar.SupportedComponents ??= [];
                calendar.SupportedReports ??= [];
            }

            return state;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Logger.Warning(ex, "CalDAV state file {Path} is unreadable; it is rebuilt by the next synchronization", path);
            return new CalDavAccountState();
        }
    }

    private static async Task WriteAsync(string path, CalDavAccountState state, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temporaryPath = path + ".tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, state, CalDavStateJsonContext.Default.CalDavAccountState, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporaryPath, path, overwrite: true);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(CalDavAccountState))]
internal sealed partial class CalDavStateJsonContext : JsonSerializerContext;
