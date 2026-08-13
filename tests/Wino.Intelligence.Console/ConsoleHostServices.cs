using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Wino.Core.Domain.Interfaces;

namespace Wino.Intelligence.ConsoleApp;

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

internal class ConsolePreferencesProxy : DispatchProxy
{
    private readonly ConcurrentDictionary<string, object?> _values = new(StringComparer.Ordinal);

    public static IPreferencesService Create()
    {
        var preferences = Create<IPreferencesService, ConsolePreferencesProxy>();
        var proxy = (ConsolePreferencesProxy)(object)preferences;
        proxy._values[nameof(IPreferencesService.DiagnosticId)] = $"intelligence-console-{Environment.MachineName}";
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
            var propertyName = targetMethod.Name[4..];
            return _values.TryGetValue(propertyName, out var value)
                ? value
                : DefaultValue(targetMethod.ReturnType);
        }

        if (targetMethod.Name.StartsWith("add_", StringComparison.Ordinal) ||
            targetMethod.Name.StartsWith("remove_", StringComparison.Ordinal))
        {
            return null;
        }

        if (targetMethod.Name == nameof(IPreferencesService.ExportPreferences))
            return "{}";
        if (targetMethod.Name == nameof(IPreferencesService.ImportPreferences))
            return (0, 0);

        return DefaultValue(targetMethod.ReturnType);
    }

    private static object? DefaultValue(Type type)
        => type == typeof(void) ? null : type.IsValueType ? Activator.CreateInstance(type) : null;
}

internal class ConsoleDefaultProxy<T> : DispatchProxy where T : class
{
    public static T Create() => DispatchProxy.Create<T, ConsoleDefaultProxy<T>>();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        var returnType = targetMethod.ReturnType;
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

internal sealed class ConsoleNativeAppService : INativeAppService, IAppMetadataService
{
    private const uint WsOverlapped = 0x00000000;
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
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        return Task.FromResult(true);
    }

    public bool IsAppRunning() => true;
    public string GetFullAppVersion() => AppVersion;
    public Task PinAppToTaskbarAsync() => Task.CompletedTask;
    public string GetCalendarAttachmentsFolderPath() => Path.Combine(_applicationDataFolder, "CalendarAttachments");

    private static IntPtr ResolveOwnerWindow()
    {
        var handle = GetConsoleWindow();
        if (handle != IntPtr.Zero)
            return handle;

        handle = Process.GetCurrentProcess().MainWindowHandle;
        return handle != IntPtr.Zero
            ? handle
            : CreateWindowEx(0, "STATIC", "Wino Intelligence Console", WsOverlapped, 0, 0, 1, 1,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

}
