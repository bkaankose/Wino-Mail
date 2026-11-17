using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Voice;
using Wino.Mail.ViewModels.Activation;

namespace Wino.Hosting;

/// <summary>There is no shell window: requests to show it are recorded and refused, so activation routing stops there.</summary>
internal sealed class HostApplicationShellHost(HostInteractionLog log) : IApplicationShellHost
{
    public bool IsShellActive(WinoApplicationMode mode) => false;

    public Task<bool> EnsureShellAsync(WinoApplicationMode mode, object? parameter = null)
    {
        log.Record(HostInteractionKind.Navigation, nameof(EnsureShellAsync), mode.ToString(), "no shell");
        return Task.FromResult(false);
    }

    public Task ShowSettingsAsync(WinoPage page, object? parameter)
    {
        log.Record(HostInteractionKind.Navigation, nameof(ShowSettingsAsync), page.ToString(), "no shell");
        return Task.CompletedTask;
    }

    public Task<bool> OpenMailFileAsync(string path, byte[] content)
    {
        log.Record(HostInteractionKind.Navigation, nameof(OpenMailFileAsync), path, "no shell");
        return Task.FromResult(false);
    }
}

/// <summary>A host has no audio input.</summary>
internal sealed class HostMicrophoneService : IMicrophoneService
{
    public event EventHandler? DevicesChanged
    {
        add { }
        remove { }
    }

    public Task<IReadOnlyList<MicrophoneDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<MicrophoneDevice>>(Array.Empty<MicrophoneDevice>());

    public MicrophoneAccess GetAccessStatus() => MicrophoneAccess.NoDevice;

    public Task<MicrophoneSessionResult> StartLevelMonitorAsync(string? deviceId, Action<double> levelChanged, CancellationToken cancellationToken = default)
        => Task.FromResult(MicrophoneSessionResult.Failed(MicrophoneAccess.NoDevice));

    public Task<MicrophoneSessionResult> StartCaptureAsync(string? deviceId, int sampleRate, Action<ReadOnlyMemory<byte>> audioAvailable,
        Action<double> levelChanged, CancellationToken cancellationToken = default)
        => Task.FromResult(MicrophoneSessionResult.Failed(MicrophoneAccess.NoDevice));
}
