using System;

namespace Wino.Mail.Uwp.Models;

public sealed class PopOutRequestedEventArgs : EventArgs
{
    public static PopOutRequestedEventArgs Default { get; } = new();
}
