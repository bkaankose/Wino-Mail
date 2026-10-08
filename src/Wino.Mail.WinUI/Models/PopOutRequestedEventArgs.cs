using System;

namespace Wino.Mail.WinUI.Models;

public sealed class PopOutRequestedEventArgs : EventArgs
{
    public static PopOutRequestedEventArgs Default { get; } = new();
}
