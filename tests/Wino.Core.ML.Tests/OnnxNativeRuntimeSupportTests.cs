using System.Runtime.InteropServices;
using FluentAssertions;
using Xunit;

namespace Wino.Core.ML.Tests;

public sealed class OnnxNativeRuntimeSupportTests : IDisposable
{
    private static readonly Version NewRuntime = new(14, 51, 36247, 0);
    private static readonly Version OldRuntime = new(14, 38, 33135, 0);

    private readonly string _root = Directory.CreateTempSubdirectory("wino-onnx-runtime-").FullName;
    private readonly string _applicationDirectory;
    private readonly string _systemDirectory;

    public OnnxNativeRuntimeSupportTests()
    {
        _applicationDirectory = Directory.CreateDirectory(Path.Combine(_root, "app")).FullName;
        _systemDirectory = Directory.CreateDirectory(Path.Combine(_root, "system")).FullName;
    }

    [Fact]
    public void RequiredLibraries_MatchOnnxRuntimeImportsPerArchitecture()
    {
        OnnxNativeRuntimeSupport.GetRequiredRuntimeLibraries(Architecture.X64)
            .Should().BeEquivalentTo("msvcp140.dll", "msvcp140_1.dll", "vcruntime140.dll", "vcruntime140_1.dll");
        OnnxNativeRuntimeSupport.GetRequiredRuntimeLibraries(Architecture.Arm64)
            .Should().BeEquivalentTo("msvcp140.dll", "msvcp140_1.dll", "vcruntime140.dll");
        OnnxNativeRuntimeSupport.GetRequiredRuntimeLibraries(Architecture.X86).Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_BundledRuntimeIsAvailable()
    {
        Touch(_applicationDirectory, "onnxruntime.dll");
        var versions = TouchRuntime(_applicationDirectory, NewRuntime);

        var status = OnnxNativeRuntimeSupport.Evaluate(Architecture.X64, _applicationDirectory, _systemDirectory, [], Lookup(versions));

        status.IsAvailable.Should().BeTrue();
        status.UnavailableReason.Should().BeNull();
    }

    [Fact]
    public void Evaluate_MissingOnnxRuntimeIsUnavailable()
    {
        var versions = TouchRuntime(_applicationDirectory, NewRuntime);

        var status = OnnxNativeRuntimeSupport.Evaluate(Architecture.X64, _applicationDirectory, _systemDirectory, [], Lookup(versions));

        status.IsAvailable.Should().BeFalse();
        status.UnavailableReason.Should().Contain("onnxruntime.dll");
    }

    [Fact]
    public void Evaluate_MissingRuntimeLibraryIsUnavailable()
    {
        Touch(_applicationDirectory, "onnxruntime.dll");
        var versions = TouchRuntime(_applicationDirectory, NewRuntime);
        File.Delete(Path.Combine(_applicationDirectory, "vcruntime140_1.dll"));

        var status = OnnxNativeRuntimeSupport.Evaluate(Architecture.X64, _applicationDirectory, _systemDirectory, [], Lookup(versions));

        status.IsAvailable.Should().BeFalse();
        status.UnavailableReason.Should().Contain("vcruntime140_1.dll").And.Contain("not found");
    }

    [Fact]
    public void Evaluate_OldSystemRuntimeWithoutBundledCopyIsUnavailable()
    {
        Touch(_applicationDirectory, "onnxruntime.dll");
        var versions = TouchRuntime(_systemDirectory, OldRuntime);

        var status = OnnxNativeRuntimeSupport.Evaluate(Architecture.X64, _applicationDirectory, _systemDirectory, [], Lookup(versions));

        status.IsAvailable.Should().BeFalse();
        status.UnavailableReason.Should().Contain("msvcp140.dll (system)").And.Contain("14.40");
    }

    [Fact]
    public void Evaluate_BundledRuntimeWinsOverOldSystemRuntime()
    {
        Touch(_applicationDirectory, "onnxruntime.dll");
        var versions = TouchRuntime(_applicationDirectory, NewRuntime);
        foreach (var pair in TouchRuntime(_systemDirectory, OldRuntime))
            versions[pair.Key] = pair.Value;

        var status = OnnxNativeRuntimeSupport.Evaluate(Architecture.X64, _applicationDirectory, _systemDirectory, [], Lookup(versions));

        status.IsAvailable.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_AlreadyLoadedOldRuntimeWinsOverBundledCopy()
    {
        Touch(_applicationDirectory, "onnxruntime.dll");
        var versions = TouchRuntime(_applicationDirectory, NewRuntime);
        var loadedDirectory = Directory.CreateDirectory(Path.Combine(_root, "injected")).FullName;
        var loaded = Touch(loadedDirectory, "MSVCP140.dll");
        versions[loaded] = OldRuntime;

        var status = OnnxNativeRuntimeSupport.Evaluate(Architecture.X64, _applicationDirectory, _systemDirectory, [loaded], Lookup(versions));

        status.IsAvailable.Should().BeFalse();
        status.UnavailableReason.Should().Contain("msvcp140.dll (already loaded)");
    }

    [Fact]
    public void Evaluate_UnreadableVersionIsUnavailable()
    {
        Touch(_applicationDirectory, "onnxruntime.dll");
        TouchRuntime(_applicationDirectory, NewRuntime);

        var status = OnnxNativeRuntimeSupport.Evaluate(Architecture.Arm64, _applicationDirectory, _systemDirectory, [], _ => null);

        status.IsAvailable.Should().BeFalse();
        status.UnavailableReason.Should().Contain("could not be read");
    }

    [Fact]
    public void Status_ReportsTheRuntimeShippedWithTheTestOutput()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture == Architecture.X86)
            return;

        // The ML project copies its bundled runtime next to onnxruntime.dll in every consumer's output.
        foreach (var library in OnnxNativeRuntimeSupport.GetRequiredRuntimeLibraries(RuntimeInformation.ProcessArchitecture))
            File.Exists(Path.Combine(AppContext.BaseDirectory, library)).Should().BeTrue(library);

        OnnxNativeRuntimeSupport.Status.IsAvailable.Should().BeTrue(OnnxNativeRuntimeSupport.Status.UnavailableReason);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static Func<string, Version?> Lookup(Dictionary<string, Version> versions) =>
        path => versions.TryGetValue(path, out var version) ? version : null;

    private static Dictionary<string, Version> TouchRuntime(string directory, Version version)
    {
        var versions = new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in OnnxNativeRuntimeSupport.GetRequiredRuntimeLibraries(Architecture.X64))
            versions[Touch(directory, library)] = version;

        return versions;
    }

    private static string Touch(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, []);
        return path;
    }
}
