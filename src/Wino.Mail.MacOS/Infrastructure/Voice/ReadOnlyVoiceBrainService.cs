#if DEBUG
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Voice;
using Wino.Mail.Api.Contracts.Voice;

namespace Wino.Mail.MacOS.Infrastructure.Voice;

/// <summary>
/// Debug launches with <c>--voice-brain-read-only</c> use this in place of the shared voice brain service, so testing a
/// signed-in build never changes the Wino Account's voice brain: it never pushes mailbox sections, never changes the
/// mode, and never saves or deletes facts. The mode comes from the device cache; the brain can still be downloaded to
/// show "What Wino remembers".
/// </summary>
internal sealed class ReadOnlyVoiceBrainService(IWinoAccountApiClient api, IVoiceBrainStore store) : IVoiceBrainService
{
    public const string LaunchArgument = "--voice-brain-read-only";

    private VoiceMode _mode = VoiceMode.Private;

    public VoiceMode CurrentMode => _mode;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        Serilog.Log.Information("The voice brain is read-only for this launch ({Argument}).", LaunchArgument);
        if (await store.GetVoiceValueAsync("mode", cancellationToken).ConfigureAwait(false) is { } cached
            && Enum.TryParse(cached, out VoiceMode mode))
            _mode = mode;
    }

    public void Stop()
    {
    }

    public Task<VoiceMode> RefreshModeAsync(CancellationToken cancellationToken = default) => Task.FromResult(_mode);

    public Task<bool> SetModeAsync(VoiceMode mode, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public void ApplyReportedMode(VoiceMode mode) => _mode = mode;

    public void RequestPush()
    {
    }

    public void NotifyVoiceStarting()
    {
    }

    /// <summary>A plain download; the real service would also apply the downloaded mode, which can start a push.</summary>
    public async Task<VoiceBrainSnapshot?> GetBrainAsync(CancellationToken cancellationToken = default)
    {
        var envelope = await api.GetVoiceBrainAsync(cancellationToken).ConfigureAwait(false);
        return envelope.IsSuccess && envelope.Result is { } brain ? new VoiceBrainSnapshot(brain, IsCached: false) : null;
    }

    public Task AddFactAsync(AddBrainFactRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<bool> DeleteFactAsync(string factId, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<bool> DeleteBrainAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
}
#endif
