using System;

namespace Wino.Platform.Windows;

/// <summary>
/// The thread a Windows service must run single-threaded-apartment calls on (launcher, clipboard, Attachment
/// Services). The head supplies it from its UI dispatcher queue, so this project needs no XAML or dispatcher type.
/// </summary>
public interface IWindowsThreadQueue
{
    bool HasThreadAccess { get; }

    /// <returns>False when the queue is shutting down and did not accept the work.</returns>
    bool TryEnqueue(Action action);
}
