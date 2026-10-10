using AppKit;
using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.MacOS.Services;

public sealed class MacTaskCompletionSound(IDispatcher dispatcher) : ITaskCompletionSound
{
    public void Play() => _ = dispatcher.ExecuteOnUIThread(() => AppKitFramework.NSBeep());
}

