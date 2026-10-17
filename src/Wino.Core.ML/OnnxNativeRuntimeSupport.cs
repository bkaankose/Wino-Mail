#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Wino.Core.ML;

/// <summary>
/// Checks that the Visual C++ runtime onnxruntime.dll links against can be loaded safely.
/// </summary>
/// <remarks>
/// onnxruntime.dll is built with a Visual Studio 2022 17.10+ toolset and dynamically links msvcp140.dll and
/// vcruntime140.dll. Since toolset 14.40 the STL's std::mutex constructor is constexpr, and a binary built that
/// way crashes with an access violation inside msvcp140.dll when an older runtime is loaded instead. No managed
/// handler sees that crash. Wino ships the runtime next to the application, but a copy that is already loaded in
/// the process wins over the bundled one, so this check inspects the copy the loader will actually use and reports
/// the model unavailable instead of letting ONNX load.
/// </remarks>
internal static class OnnxNativeRuntimeSupport
{
    internal const string OnnxRuntimeLibraryFileName = "onnxruntime.dll";

    /// <summary>First Visual C++ runtime whose msvcp140.dll matches the constexpr std::mutex layout.</summary>
    internal static readonly Version MinimumRuntimeVersion = new(14, 40);

    private static readonly Lazy<OnnxNativeRuntimeStatus> CurrentStatus =
        new(EvaluateCurrentProcess, LazyThreadSafetyMode.ExecutionAndPublication);

    public static OnnxNativeRuntimeStatus Status => CurrentStatus.Value;

    /// <summary>The runtime libraries onnxruntime.dll imports for the given architecture.</summary>
    internal static string[] GetRequiredRuntimeLibraries(Architecture architecture) => architecture switch
    {
        Architecture.X64 => ["msvcp140.dll", "msvcp140_1.dll", "vcruntime140.dll", "vcruntime140_1.dll"],
        Architecture.Arm64 => ["msvcp140.dll", "msvcp140_1.dll", "vcruntime140.dll"],
        _ => []
    };

    private static OnnxNativeRuntimeStatus EvaluateCurrentProcess()
    {
        if (!OperatingSystem.IsWindows())
            return OnnxNativeRuntimeStatus.Available;

        try
        {
            return Evaluate(
                RuntimeInformation.ProcessArchitecture,
                AppContext.BaseDirectory,
                Environment.SystemDirectory,
                GetLoadedModulePaths(),
                ReadFileVersion);
        }
        catch (Exception ex)
        {
            return OnnxNativeRuntimeStatus.Unavailable($"The native runtime check failed: {ex.GetType().Name}.");
        }
    }

    /// <summary>
    /// Mirrors the Windows loader's search order for the imports of onnxruntime.dll: a module that is already
    /// loaded is reused whatever its path, otherwise the application directory is searched before the system one.
    /// </summary>
    internal static OnnxNativeRuntimeStatus Evaluate(
        Architecture architecture,
        string applicationDirectory,
        string systemDirectory,
        IReadOnlyCollection<string> loadedModulePaths,
        Func<string, Version?> readFileVersion)
    {
        var requiredLibraries = GetRequiredRuntimeLibraries(architecture);
        if (requiredLibraries.Length == 0)
            return OnnxNativeRuntimeStatus.Unavailable($"Local ML inference is not supported on {architecture}.");

        if (!File.Exists(Path.Combine(applicationDirectory, OnnxRuntimeLibraryFileName)))
            return OnnxNativeRuntimeStatus.Unavailable($"{OnnxRuntimeLibraryFileName} is missing from the application directory.");

        foreach (var library in requiredLibraries)
        {
            var resolved = ResolveLibrary(library, applicationDirectory, systemDirectory, loadedModulePaths);
            if (resolved is null)
                return OnnxNativeRuntimeStatus.Unavailable($"{library} was not found next to the application or in the system directory.");

            var (path, origin) = resolved.Value;
            var version = readFileVersion(path);
            if (version is null)
                return OnnxNativeRuntimeStatus.Unavailable($"The version of {library} ({origin}) could not be read.");

            if (version < MinimumRuntimeVersion)
            {
                return OnnxNativeRuntimeStatus.Unavailable(
                    $"{library} ({origin}) is version {version}; onnxruntime needs the Visual C++ runtime {MinimumRuntimeVersion} or newer.");
            }
        }

        return OnnxNativeRuntimeStatus.Available;
    }

    private static (string Path, string Origin)? ResolveLibrary(
        string library,
        string applicationDirectory,
        string systemDirectory,
        IReadOnlyCollection<string> loadedModulePaths)
    {
        foreach (var modulePath in loadedModulePaths)
        {
            if (string.Equals(Path.GetFileName(modulePath), library, StringComparison.OrdinalIgnoreCase))
                return (modulePath, "already loaded");
        }

        var bundled = Path.Combine(applicationDirectory, library);
        if (File.Exists(bundled))
            return (bundled, "bundled");

        var system = Path.Combine(systemDirectory, library);
        return File.Exists(system) ? (system, "system") : null;
    }

    private static List<string> GetLoadedModulePaths()
    {
        var paths = new List<string>();

        try
        {
            using var process = Process.GetCurrentProcess();
            foreach (ProcessModule module in process.Modules)
            {
                using (module)
                {
                    if (!string.IsNullOrEmpty(module.FileName))
                        paths.Add(module.FileName);
                }
            }
        }
        catch (Exception)
        {
            // Module enumeration is best effort; the on-disk search order still applies.
        }

        return paths;
    }

    private static Version? ReadFileVersion(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return new Version(
                Math.Max(info.FileMajorPart, 0),
                Math.Max(info.FileMinorPart, 0),
                Math.Max(info.FileBuildPart, 0),
                Math.Max(info.FilePrivatePart, 0));
        }
        catch (Exception)
        {
            return null;
        }
    }
}

internal sealed record OnnxNativeRuntimeStatus(bool IsAvailable, string? UnavailableReason)
{
    public static OnnxNativeRuntimeStatus Available { get; } = new(true, null);

    public static OnnxNativeRuntimeStatus Unavailable(string reason) => new(false, reason);
}
