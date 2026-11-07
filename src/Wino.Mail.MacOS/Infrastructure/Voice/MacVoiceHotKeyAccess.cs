using Wino.Core.Domain.Models;
using Wino.Mail.ViewModels.Voice;
using Wino.Platform.MacOS.Services;

namespace Wino.Mail.MacOS.Infrastructure.Voice;

/// <summary>
/// Lets the Voice Control settings page try and pause the voice shortcut (Windows App.TryConfigureVoiceHotKey). The
/// app delegate attaches the bubble once it exists; before that a valid shortcut is accepted and stays unregistered.
/// </summary>
public sealed class MacVoiceHotKeyAccess
{
    private VoiceBubbleController? _controller;
    private MacVoiceBubbleHost? _host;

    internal void Attach(VoiceBubbleController? controller, MacVoiceBubbleHost? host)
    {
        _controller = controller;
        _host = host;
    }

    /// <summary>Registers <paramref name="gesture"/> when voice control is usable and <paramref name="enabled"/>; false when the system refused it.</summary>
    public bool TryConfigure(bool enabled, HotKeyGesture gesture)
        => _controller?.TryConfigureHotKey(enabled, gesture)
           ?? (gesture.Normalize() is { IsValid: true } normalized && MacHotKeyKeys.TryGetKeyCode(normalized.Key, out _));

    /// <summary>Removes the shortcut while the recorder listens, since a registered shortcut never reaches the window.</summary>
    public void Suspend() => _host?.TrySetHotKey(null);
}
