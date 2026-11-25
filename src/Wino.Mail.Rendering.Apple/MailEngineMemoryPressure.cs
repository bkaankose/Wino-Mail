using CoreFoundation;
using Wino.Renderer;

namespace Wino.Mail.Rendering.Apple;

/// <summary>
/// Gives the engine's decoded images and caches back when the system runs short of memory (on iOS a warning comes
/// before the app is ended). Start it once per process.
/// </summary>
public sealed class MailEngineMemoryPressure : IDisposable
{
    private readonly DispatchSource.MemoryPressure _source;

    public MailEngineMemoryPressure(MailEngineHost engineHost)
    {
        _source = new DispatchSource.MemoryPressure(MemoryPressureFlags.Warn | MemoryPressureFlags.Critical, DispatchQueue.MainQueue);
        _source.SetEventHandler(() =>
            engineHost.Trim(_source.PressureFlags.HasFlag(MemoryPressureFlags.Critical) ? MemoryTrimLevel.Critical : MemoryTrimLevel.Low));
        _source.Resume();
    }

    public void Dispose() => _source.Cancel();
}
