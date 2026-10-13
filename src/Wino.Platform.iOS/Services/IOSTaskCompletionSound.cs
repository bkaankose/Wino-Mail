using AudioToolbox;
using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.iOS.Services;

/// <summary>A short system tick ("Tink", system sound 1057) when a task is completed; silent with the ring/silent switch off.</summary>
public sealed class IOSTaskCompletionSound : ITaskCompletionSound
{
    private const uint TinkSoundId = 1057;
    private static readonly Lazy<SystemSound> Sound = new(() => new SystemSound(TinkSoundId));

    public void Play() => Sound.Value.PlaySystemSound();
}
