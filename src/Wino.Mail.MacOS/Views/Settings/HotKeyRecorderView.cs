using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Platform.MacOS.Services;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// The global shortcut recorder (Windows WinoHotKeyInput), 28pt high: empty (press keys here),
/// recording (accent ring, red dot, "esc to cancel"), committed (key caps in ⌃⌥⇧⌘ order and a
/// clear button) and error (red ring, the refused combination and "Not available"). Click, Return
/// or Space starts listening; Escape or leaving the field cancels. The owner decides what a
/// captured combination means and calls <see cref="SetGesture"/> or <see cref="ShowRefused"/>.
/// </summary>
internal sealed class HotKeyRecorderView : WinoSurfaceView
{
    private enum RecorderState { Empty, Recording, Committed, Refused }

    private readonly NSStackView _caps;
    private readonly NSTextField _placeholder;
    private readonly WinoSurfaceView _recordingDot;
    private readonly NSTextField _hint;
    private readonly NSButton _clear;
    private RecorderState _state = RecorderState.Empty;
    private HotKeyGesture? _gesture;
    private bool _enabled = true;

    public HotKeyRecorderView()
    {
        Fill = NSColor.TextBackground;
        Stroke = WinoStyle.GroupStroke;
        CornerRadius = WinoStyle.ControlRadius;
        TranslatesAutoresizingMaskIntoConstraints = false;

        _recordingDot = new WinoSurfaceView { Fill = WinoStyle.Critical, CornerRadius = 3, Hidden = true };
        WinoLayout.Size(_recordingDot, 6, 6);
        _caps = WinoLayout.HStack(3);
        _placeholder = WinoStyle.Label(Translator.KeyboardShortcuts_PressKeysHere, WinoStyle.Body, WinoStyle.TertiaryText);
        _hint = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.TertiaryText);
        _hint.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        _clear = new NSButton
        {
            Bordered = false,
            Image = WinoIcons.Image(WinoIconGlyph.Dismiss, 10, null, Translator.HotKeyRecorder_Clear),
            ImagePosition = NSCellImagePosition.ImageOnly,
            ContentTintColor = WinoStyle.SecondaryText,
            ToolTip = Translator.HotKeyRecorder_Clear,
            TranslatesAutoresizingMaskIntoConstraints = false,
            Hidden = true
        };
        WinoLayout.Size(_clear, 18, 18);
        WinoAccessibility.Label(_clear, Translator.HotKeyRecorder_Clear);
        _clear.Activated += (_, _) => Cleared?.Invoke(this, EventArgs.Empty);

        var row = WinoLayout.HStack(6, _recordingDot, _caps, _placeholder, WinoLayout.Spacer(), _hint, _clear);
        row.DetachesHiddenViews = true;
        WinoLayout.Fill(row, this, 0, 8, 0, 6);
        HeightAnchor.ConstraintEqualTo(28).Active = true;
        WidthAnchor.ConstraintGreaterThanOrEqualTo(240).Active = true;

        AccessibilityElement = true;
        AccessibilityRole = NSAccessibilityRoles.TextFieldRole;
        AccessibilityLabel = Translator.CompanionSettings_HotKey_KeyCombination;
        Render();
    }

    /// <summary>Listening started: the owner removes the system registration so the current combination can be captured.</summary>
    public event EventHandler? CaptureStarted;

    /// <summary>Listening ended without a combination (Escape, click elsewhere).</summary>
    public event EventHandler? CaptureCanceled;

    /// <summary>A key with its modifiers was pressed while listening.</summary>
    public event EventHandler<HotKeyGesture>? Committed;

    /// <summary>The clear button was pressed.</summary>
    public event EventHandler? Cleared;

    public bool IsRecording => _state == RecorderState.Recording;

    public bool IsEnabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (!value && IsRecording) EndRecording(cancel: true);
            AlphaValue = value ? 1 : (nfloat)0.5;
            _clear.Enabled = value;
        }
    }

    /// <summary>Shows a stored combination, or the empty prompt for null.</summary>
    public void SetGesture(HotKeyGesture? gesture)
    {
        _gesture = gesture;
        _state = gesture is null ? RecorderState.Empty : RecorderState.Committed;
        Render();
    }

    /// <summary>Shows <paramref name="refused"/> in the error state; the next capture clears it.</summary>
    public void ShowRefused(HotKeyGesture refused)
    {
        _gesture = refused;
        _state = RecorderState.Refused;
        Render();
    }

    /// <summary>Starts listening (debug bridge and keyboard).</summary>
    public void BeginRecording()
    {
        if (!_enabled || IsRecording) return;
        if (Window?.FirstResponder != this) Window?.MakeFirstResponder(this);
        _state = RecorderState.Recording;
        Render();
        CaptureStarted?.Invoke(this, EventArgs.Empty);
    }

    private void EndRecording(bool cancel)
    {
        if (!IsRecording) return;
        _state = _gesture is null ? RecorderState.Empty : RecorderState.Committed;
        Render();
        if (cancel) CaptureCanceled?.Invoke(this, EventArgs.Empty);
    }

    private void Render(ModifierKeys liveModifiers = ModifierKeys.None)
    {
        foreach (var view in _caps.ArrangedSubviews)
        {
            _caps.RemoveArrangedSubview(view);
            view.RemoveFromSuperview();
            view.Dispose();
        }

        var recording = _state == RecorderState.Recording;
        _recordingDot.Hidden = !recording;
        Stroke = recording ? WinoStyle.Accent : _state == RecorderState.Refused ? WinoStyle.Critical : WinoStyle.GroupStroke;
        StrokeWidth = recording || _state == RecorderState.Refused ? 2 : 1;

        if (recording)
        {
            foreach (var symbol in MacHotKeyKeys.ModifierSymbols(liveModifiers)) _caps.AddArrangedSubview(Cap(symbol));
            _placeholder.StringValue = Translator.CompanionSettings_HotKey_Listening;
            _placeholder.TextColor = WinoStyle.SecondaryText;
            _placeholder.Hidden = liveModifiers != ModifierKeys.None;
            _hint.StringValue = Translator.HotKeyRecorder_EscToCancel;
            _hint.TextColor = WinoStyle.TertiaryText;
            _hint.Hidden = false;
            _clear.Hidden = true;
            AccessibilityValue = new Foundation.NSString(Translator.CompanionSettings_HotKey_Listening);
            ToolTip = null;
            return;
        }

        if (_gesture is not { } gesture)
        {
            _placeholder.StringValue = Translator.KeyboardShortcuts_PressKeysHere;
            _placeholder.TextColor = WinoStyle.TertiaryText;
            _placeholder.Hidden = false;
            _hint.Hidden = true;
            _clear.Hidden = true;
            AccessibilityValue = new Foundation.NSString(string.Empty);
            ToolTip = null;
            return;
        }

        foreach (var symbol in MacHotKeyKeys.ModifierSymbols(gesture.Modifiers)) _caps.AddArrangedSubview(Cap(symbol));
        _caps.AddArrangedSubview(Cap(MacHotKeyKeys.Display(gesture.Key)));
        _placeholder.Hidden = true;
        var refused = _state == RecorderState.Refused;
        _hint.StringValue = refused ? Translator.HotKeyRecorder_NotAvailable : string.Empty;
        _hint.TextColor = WinoStyle.Critical;
        _hint.Hidden = !refused;
        _clear.Hidden = false;
        AccessibilityValue = new Foundation.NSString(MacHotKeyKeys.Format(gesture));
        ToolTip = refused ? Translator.HotKeyRecorder_NotAvailable : Translator.HotKeyRecorder_ClickToChange;
    }

    private static NSView Cap(string text)
    {
        var cap = new WinoSurfaceView
        {
            Fill = WinoStyle.Dynamic(WinoStyle.Hex(0xF2F2F4), WinoStyle.Hex(0xFFFFFF, 0.08)),
            Stroke = WinoStyle.GroupStroke,
            CornerRadius = 4
        };
        var label = WinoStyle.Label(text, NSFont.SystemFontOfSize(11, NSFontWeight.Medium), WinoStyle.PrimaryText);
        label.Alignment = NSTextAlignment.Center;
        WinoLayout.Fill(label, cap, 1, 5, 1, 5);
        cap.HeightAnchor.ConstraintEqualTo(18).Active = true;
        cap.WidthAnchor.ConstraintGreaterThanOrEqualTo(18).Active = true;
        return cap;
    }

    public override bool AcceptsFirstResponder() => _enabled;

    public override bool AcceptsFirstMouse(NSEvent? theEvent) => true;

    public override bool ResignFirstResponder()
    {
        EndRecording(cancel: true);
        return true;
    }

    public override void MouseDown(NSEvent theEvent)
    {
        if (!_enabled) return;
        Window?.MakeFirstResponder(this);
        BeginRecording();
    }

    public override bool PerformKeyEquivalent(NSEvent theEvent)
    {
        // Command combinations reach menus first; capture them while listening.
        if (!IsRecording || Window?.FirstResponder != this) return false;
        KeyDown(theEvent);
        return true;
    }

    public override void FlagsChanged(NSEvent theEvent)
    {
        if (IsRecording) Render(ToModifiers(theEvent.ModifierFlags));
        else base.FlagsChanged(theEvent);
    }

    public override void KeyDown(NSEvent theEvent)
    {
        var modifiers = ToModifiers(theEvent.ModifierFlags);
        if (!IsRecording)
        {
            // Return or Space starts listening, like pressing a button.
            if (_enabled && modifiers == ModifierKeys.None && theEvent.KeyCode is 36 or 49) BeginRecording();
            else base.KeyDown(theEvent);
            return;
        }

        if (theEvent.KeyCode == 53 && modifiers == ModifierKeys.None)
        {
            EndRecording(cancel: true);
            return;
        }

        var key = MacHotKeyKeys.NameForKeyCode(theEvent.KeyCode);
        if (key is null)
        {
            AppKitFramework.NSBeep();
            return;
        }

        _state = _gesture is null ? RecorderState.Empty : RecorderState.Committed;
        Render();
        Committed?.Invoke(this, new HotKeyGesture(key, modifiers));
    }

    private static ModifierKeys ToModifiers(NSEventModifierMask flags)
    {
        var modifiers = ModifierKeys.None;
        if (flags.HasFlag(NSEventModifierMask.ControlKeyMask)) modifiers |= ModifierKeys.Control;
        if (flags.HasFlag(NSEventModifierMask.AlternateKeyMask)) modifiers |= ModifierKeys.Alt;
        if (flags.HasFlag(NSEventModifierMask.ShiftKeyMask)) modifiers |= ModifierKeys.Shift;
        if (flags.HasFlag(NSEventModifierMask.CommandKeyMask)) modifiers |= ModifierKeys.Command;
        return modifiers;
    }

    public override bool AccessibilityPerformPress()
    {
        BeginRecording();
        return true;
    }
}
