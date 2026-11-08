#nullable enable
using System;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models;
using Wino.Mail.Controls.Core.VoiceBubble;

namespace Wino.Mail.ViewModels.Voice;

/// <summary>
/// The platform window around the voice bubble: where it opens, the system-wide shortcut, the frame clock that drives
/// the voice bars, and the UI thread. <see cref="VoiceBubbleController"/> runs the flow through it.
/// </summary>
public interface IVoiceBubbleHost
{
    IVoiceBubbleView Bubble { get; }

    /// <summary>Opens the window at <paramref name="position"/>; the bubble then animates in from its state.</summary>
    void Show(VoiceBubblePosition position);

    /// <summary>Hides the window at once, without the exit animation.</summary>
    void Hide();

    /// <summary>Whether the platform can register <paramref name="gesture"/> at all, without registering it.</summary>
    bool CanRegisterHotKey(HotKeyGesture gesture);

    /// <summary>
    /// Registers <paramref name="gesture"/> as the shortcut that opens the bubble in <paramref name="mode"/>, or removes
    /// that shortcut when it is null. False when the system refused.
    /// </summary>
    bool TrySetHotKey(VoiceBubbleInputMode mode, HotKeyGesture? gesture);

    /// <summary>A registered shortcut was pressed; the argument says which.</summary>
    event EventHandler<VoiceBubbleInputMode>? HotKeyPressed;

    /// <summary>Starts raising <see cref="FrameRendered"/> once per display frame.</summary>
    void StartFrames();

    void StopFrames();

    /// <summary>Raised on the UI thread with the seconds since the previous frame (0 for the first).</summary>
    event EventHandler<double>? FrameRendered;

    /// <summary>Runs <paramref name="action"/> on the UI thread, inline when already on it.</summary>
    void Post(Action action);
}
