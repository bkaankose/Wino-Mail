using System;
using Microsoft.UI.Dispatching;
using Wino.Platform.Windows;

namespace Wino.Mail.WinUI.Services;

internal sealed class DispatcherQueueThreadQueue(DispatcherQueue dispatcherQueue) : IWindowsThreadQueue
{
    public bool HasThreadAccess => dispatcherQueue.HasThreadAccess;

    public bool TryEnqueue(Action action) => dispatcherQueue.TryEnqueue(() => action());
}
