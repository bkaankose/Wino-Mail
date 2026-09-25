using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Intelligence.ConsoleApp.Hosting;

// Stand-ins for the services only the WinUI project implements. They exist so the shared
// services resolve. None of them reproduce UI behaviour.

internal sealed class ConsoleConfigurationService : IConfigurationService
{
    private readonly ConcurrentDictionary<string, object?> _local = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, object?> _roaming = new(StringComparer.Ordinal);

    public bool Contains(string key) => _local.ContainsKey(key);
    public bool Remove(string key) => _local.TryRemove(key, out _);
    public void Set(string key, object value) => _local[key] = value;
    public T Get<T>(string key, T defaultValue = default!) => Get(_local, key, defaultValue);
    public void SetRoaming(string key, object value) => _roaming[key] = value;
    public T GetRoaming<T>(string key, T defaultValue = default!) => Get(_roaming, key, defaultValue);

    private static T Get<T>(ConcurrentDictionary<string, object?> source, string key, T defaultValue)
        => source.TryGetValue(key, out var value) && value is T typed ? typed : defaultValue;
}

internal sealed class ConsoleKeyPressService : IKeyPressService
{
    public bool IsCtrlKeyPressed() => false;
    public bool IsShiftKeyPressed() => false;
}

internal sealed class ConsoleUserPresenceStateProvider : IUserPresenceStateProvider
{
    public bool IsPresenting() => false;
    public bool IsSystemQuietTimeActive() => false;
}

/// <summary>
/// The app keeps preferences in the package settings hive, which a process without package
/// identity cannot open. Every preference here starts at its type default.
/// </summary>
internal class ConsolePreferencesProxy : DispatchProxy
{
    private readonly ConcurrentDictionary<string, object?> _values = new(StringComparer.Ordinal);

    public static IPreferencesService Create()
    {
        var preferences = Create<IPreferencesService, ConsolePreferencesProxy>();
        var proxy = (ConsolePreferencesProxy)(object)preferences;
        proxy._values[nameof(IPreferencesService.DiagnosticId)] = $"wino-intelligence-console-{Environment.MachineName}";
        proxy._values[nameof(IPreferencesService.IsLoggingEnabled)] = false;
        return preferences;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        args ??= [];

        if (targetMethod.Name.StartsWith("set_", StringComparison.Ordinal))
        {
            _values[targetMethod.Name[4..]] = args[0];
            return null;
        }

        if (targetMethod.Name.StartsWith("get_", StringComparison.Ordinal))
        {
            return _values.TryGetValue(targetMethod.Name[4..], out var value)
                ? value
                : ConsoleDefaults.For(targetMethod.ReturnType);
        }

        if (targetMethod.Name == nameof(IPreferencesService.ExportPreferences))
            return "{}";
        if (targetMethod.Name == nameof(IPreferencesService.ImportPreferences))
            return (0, 0);

        return ConsoleDefaults.For(targetMethod.ReturnType);
    }
}

/// <summary>Answers every call with a completed task or a default value.</summary>
internal class ConsoleDefaultProxy<T> : DispatchProxy where T : class
{
    public static T Create() => DispatchProxy.Create<T, ConsoleDefaultProxy<T>>();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        return ConsoleDefaults.For(targetMethod.ReturnType);
    }
}

/// <summary>
/// Services sometimes ask the user to confirm. The question is shown on the console and answered
/// there, so no confirmation is silently skipped. Messages are printed. Everything else is a no-op.
/// </summary>
internal class ConsoleDialogProxy : DispatchProxy
{
    public static IMailDialogService Create() => Create<IMailDialogService, ConsoleDialogProxy>();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        var text = string.Join(" | ", (args ?? []).OfType<string>().Where(static value => !string.IsNullOrWhiteSpace(value)));

        if (targetMethod.ReturnType == typeof(Task<bool>) &&
            targetMethod.Name.Contains("Confirmation", StringComparison.Ordinal))
        {
            return Task.FromResult(ConsoleOutput.Confirm($"[dialog] {text}"));
        }

        if (text.Length > 0 &&
            (targetMethod.Name.Contains("Message", StringComparison.Ordinal) ||
             targetMethod.Name.Contains("InfoBar", StringComparison.Ordinal)))
        {
            ConsoleOutput.Muted($"[dialog] {text}");
        }

        return ConsoleDefaults.For(targetMethod.ReturnType);
    }
}

internal static class ConsoleDefaults
{
    public static object? For(Type returnType)
    {
        if (returnType == typeof(void))
            return null;
        if (returnType == typeof(Task))
            return Task.CompletedTask;
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var resultType = returnType.GetGenericArguments()[0];
            var result = resultType.IsValueType ? Activator.CreateInstance(resultType) : null;
            return typeof(Task).GetMethod(nameof(Task.FromResult))!
                .MakeGenericMethod(resultType)
                .Invoke(null, [result]);
        }

        return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
    }
}

internal sealed class ConsoleStoreManagementService : IStoreManagementService
{
    public Task<bool> HasProductAsync(WinoAddOnProductType productType) => Task.FromResult(false);

    public Task<StorePurchaseResult> PurchaseAsync(WinoAddOnProductType productType)
        => throw new NotSupportedException("Store purchases need the packaged app.");
}

internal sealed class ConsoleNotificationBuilder : INotificationBuilder
{
    public Task CreateNotificationsAsync(IEnumerable<MailCopy> newMailItems) => Task.CompletedTask;
    public Task CreateTestNotificationsAsync(IEnumerable<MailCopy> mailItems) => Task.CompletedTask;
    public Task UpdateTaskbarIconBadgeAsync() => Task.CompletedTask;
    public Task UpdateJumpListOptionsAsync() => Task.CompletedTask;
    public Task AddCalendarTaskbarBadgeCountAsync(int newlyDownloadedCount) => Task.CompletedTask;
    public Task ClearCalendarTaskbarBadgeAsync() => Task.CompletedTask;
    public void RemoveNotification(Guid mailUniqueId) { }
    public void CreateAttentionRequiredNotification(MailAccount account)
        => ConsoleOutput.Warning($"[notification] {account.Address} needs attention: {account.AttentionReason}");
    public void CreateWebView2RuntimeMissingNotification() { }
    public Task CreateCalendarReminderNotificationAsync(CalendarItem calendarItem, long reminderDurationInSeconds) => Task.CompletedTask;
    public Task CreateTestCalendarReminderNotificationAsync(CalendarItem calendarItem) => Task.CompletedTask;
    public Task CreateTestPeopleNotificationAsync(AccountContact contact) => Task.CompletedTask;
    public Task CreateTestTaskReminderNotificationAsync(AccountTask task) => Task.CompletedTask;
}

/// <summary>
/// Supplies paths and a parent window. Interactive Microsoft sign-in (WAM) needs a window
/// handle, so the console window is used, or a hidden window when there is none.
/// </summary>
internal sealed class ConsoleNativeAppService : INativeAppService, IAppMetadataService
{
    private readonly string _applicationDataFolder;
    private readonly IntPtr _ownerWindow;

    public ConsoleNativeAppService(string applicationDataFolder)
    {
        _applicationDataFolder = applicationDataFolder;
        _ownerWindow = ResolveOwnerWindow();
        GetCoreWindowHwnd = () => _ownerWindow;
    }

    public Func<IntPtr> GetCoreWindowHwnd { get; set; }
    public string AppVersion => typeof(ConsoleNativeAppService).Assembly.GetName().Version?.ToString() ?? "1.0.0";
    public string PackageName => "Wino.Intelligence.Console";
    public string BuildConfiguration => "Debug";
    public string SentryEnvironment => "intelligence-console";
    public string SentryRelease => $"{PackageName}@{AppVersion}";
    public string SentryDist => AppVersion;

    public string GetWebAuthenticationBrokerUri() => string.Empty;

    public Task<string> GetMimeMessageStoragePath()
    {
        var path = Path.Combine(_applicationDataFolder, "Mime");
        Directory.CreateDirectory(path);
        return Task.FromResult(path);
    }

    public Task LaunchFileAsync(string filePath)
    {
        Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    public Task<bool> LaunchUriAsync(Uri uri)
    {
        // Gmail's interactive sign-in opens the browser through this.
        ConsoleOutput.Muted($"Opening {uri.GetLeftPart(UriPartial.Path)} in the browser.");
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        return Task.FromResult(true);
    }

    public bool IsAppRunning() => true;
    public string GetFullAppVersion() => AppVersion;
    public Task PinAppToTaskbarAsync() => Task.CompletedTask;
    public WindowsTaskbarPosition GetTaskbarPosition() => WindowsTaskbarPosition.Bottom;
    public string GetCalendarAttachmentsFolderPath() => Path.Combine(_applicationDataFolder, "CalendarAttachments");

    private static IntPtr ResolveOwnerWindow()
    {
        var handle = GetConsoleWindow();
        if (handle != IntPtr.Zero)
            return handle;

        return CreateWindowEx(0, "STATIC", "Wino Intelligence Console", 0, 0, 0, 1, 1,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint extendedStyle, string className, string windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
}
