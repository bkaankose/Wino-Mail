#nullable enable
using System;
using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.Windows.PushRelay;

/// <summary>Windows has no relay push channel yet: no token, and the change event is never raised.</summary>
public sealed class NullPushTokenProvider : IPushTokenProvider
{
    public bool IsPushChannelAvailable => false;
    public string? CurrentToken => null;
    public string? LastFailure => null;

    public event EventHandler? TokenChanged
    {
        add { }
        remove { }
    }
}
