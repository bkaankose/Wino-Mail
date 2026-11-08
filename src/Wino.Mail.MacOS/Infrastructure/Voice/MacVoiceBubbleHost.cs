using System.Diagnostics;
using AppKit;
using CoreAnimation;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models;
using Wino.Mail.Controls.AppKit.VoiceBubble;
using Wino.Mail.Controls.Core.VoiceBubble;
using Wino.Mail.ViewModels.Voice;
using Wino.Platform.MacOS.Services;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Infrastructure.Voice;

/// <summary>
/// The Mac window around the voice bubble: a borderless floating panel that never activates Wino, so the bubble
/// opens over whatever app the user is in and the keyboard goes to it without bringing Wino forward.
/// <see cref="VoiceBubblePosition.BottomRight"/> means the companion's place, under the menu bar icon; the other
/// positions use the corners or the center of the screen under the pointer. The panel morphs to the bubble's size,
/// growing away from its anchor. It also owns the voice shortcut, a second Carbon hotkey next to the companion's.
/// </summary>
internal sealed class MacVoiceBubbleHost : IVoiceBubbleHost, IDisposable
{
    private const uint VoiceHotKeySignature = 0x57494E56; // 'WINV'
    private const double ScreenMargin = 12;
    private const double MenuBarGap = 6;
    private const double FrameInterval = 1.0 / 60;

    private readonly VoiceBubblePanel _panel;
    private readonly WinoVoiceBubbleView _bubble;
    private const uint TextHotKeySignature = 0x57494E54; // 'WINT'
    private readonly MacGlobalHotKeyService _hotKeys = new(VoiceHotKeySignature);
    private readonly MacGlobalHotKeyService _textHotKeys = new(TextHotKeySignature);
    private readonly Func<CGRect?> _menuBarIconFrame;
    private readonly IPreferencesService _preferences;
    private readonly Stopwatch _frameClock = new();
    private NSTimer? _frameTimer;
    private TimeSpan _lastFrame;
    private CGRect _reference;
    private Anchor _horizontal = Anchor.End;
    private Anchor _vertical = Anchor.Start;
    private bool _disposed;

    private enum Anchor
    {
        Start,
        Center,
        End
    }

    public MacVoiceBubbleHost(Func<CGRect?> menuBarIconFrame, IPreferencesService preferences)
    {
        _menuBarIconFrame = menuBarIconFrame;
        _preferences = preferences;
        _bubble = new WinoVoiceBubbleView();
        _panel = new VoiceBubblePanel(_bubble);
        _bubble.TargetSizeChanged += BubbleTargetSizeChanged;
        _hotKeys.Pressed += HotKeysPressed;
        _textHotKeys.Pressed += TextHotKeysPressed;
    }

    public IVoiceBubbleView Bubble => _bubble;

    public event EventHandler<VoiceBubbleInputMode>? HotKeyPressed;

    public event EventHandler<double>? FrameRendered;

    /// <summary>The voice shortcut registered with the system, and why the last registration failed (debug bridge).</summary>
    public string DescribeHotKey()
        => $"registered={(_hotKeys.ActiveHotKey is { } active ? MacHotKeyKeys.Format(active) : "none")} error={_hotKeys.LastError ?? "none"}";

    public void SimulateHotKey() => _hotKeys.SimulatePress();

    public string DescribePanel() => $"visible={_panel.IsVisible} key={_panel.IsKeyWindow} alpha={_panel.AlphaValue:0.00} frame={_panel.Frame} state={_bubble.State}";

    public void Show(VoiceBubblePosition position)
    {
        if (_disposed)
            return;

        PlaceReference(position);
        if (!_panel.IsVisible)
            _panel.AlphaValue = 0;

        _panel.SetFrame(FrameFor(_bubble.TargetSize), false);
        _panel.OrderFrontRegardless();
        _panel.MakeKeyWindow();
    }

    public void Hide()
    {
        if (!_disposed)
            _panel.OrderOut(null);
    }

    /// <summary>
    /// A shortcut the companion already uses would be refused by the system as a duplicate inside the app, so it is
    /// refused here first.
    /// </summary>
    public bool CanRegisterHotKey(HotKeyGesture gesture)
    {
        var normalized = gesture.Normalize();
        if (!normalized.IsValid || !MacHotKeyKeys.TryGetKeyCode(normalized.Key, out _))
            return false;

        var companion = new HotKeyGesture(_preferences.CompanionHotKeyKey, _preferences.CompanionHotKeyModifiers).Normalize();
        return !(_preferences.IsCompanionHotKeyEnabled && companion == normalized);
    }

    public bool TrySetHotKey(VoiceBubbleInputMode mode, HotKeyGesture? gesture)
    {
        if (_disposed)
            return false;

        if (gesture is { } value && !CanRegisterHotKey(value))
            return false;

        return (mode == VoiceBubbleInputMode.Text ? _textHotKeys : _hotKeys).TrySetHotKey(gesture);
    }

    public void StartFrames()
    {
        if (_frameTimer is not null)
            return;

        _frameClock.Restart();
        _lastFrame = TimeSpan.Zero;
        _frameTimer = NSTimer.CreateTimer(FrameInterval, true, _ =>
        {
            var now = _frameClock.Elapsed;
            var elapsed = _lastFrame == TimeSpan.Zero ? 0 : (now - _lastFrame).TotalSeconds;
            _lastFrame = now;
            FrameRendered?.Invoke(this, elapsed);
        });
        // The common modes keep the bars moving while a menu or a drag tracks the mouse.
        NSRunLoop.Main.AddTimer(_frameTimer, NSRunLoopMode.Common);
    }

    public void StopFrames()
    {
        _frameTimer?.Invalidate();
        _frameTimer = null;
        _frameClock.Stop();
    }

    public void Post(Action action)
    {
        if (NSThread.IsMain)
            action();
        else
            NSApplication.SharedApplication.BeginInvokeOnMainThread(action);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        StopFrames();
        _bubble.TargetSizeChanged -= BubbleTargetSizeChanged;
        _hotKeys.Pressed -= HotKeysPressed;
        _hotKeys.Dispose();
        _textHotKeys.Pressed -= TextHotKeysPressed;
        _textHotKeys.Dispose();
        _panel.OrderOut(null);
        _panel.Close();
    }

    private void HotKeysPressed(object? sender, EventArgs e) => HotKeyPressed?.Invoke(this, VoiceBubbleInputMode.Voice);

    private void TextHotKeysPressed(object? sender, EventArgs e) => HotKeyPressed?.Invoke(this, VoiceBubbleInputMode.Text);

    private void BubbleTargetSizeChanged(object? sender, VoiceBubbleSizeChange change)
    {
        if (_disposed)
            return;

        var frame = FrameFor(change.Size);
        if (!change.Animate || !_panel.IsVisible)
        {
            _panel.SetFrame(frame, true);
            _panel.InvalidateShadow();
            return;
        }

        NSAnimationContext.RunAnimation(context =>
        {
            context.Duration = WinoVoiceBubbleView.MorphSeconds;
            context.TimingFunction = CAMediaTimingFunction.FromControlPoints(0.1f, 0.9f, 0.2f, 1f);
            WinoWindowAnimator.SetFrame(_panel, frame);
        }, _panel.InvalidateShadow);
    }

    private void PlaceReference(VoiceBubblePosition position)
    {
        if (position == VoiceBubblePosition.BottomRight && _menuBarIconFrame() is { } icon && ScreenFor(icon) is { } iconScreen)
        {
            // Under the menu bar icon, like the companion: centred on the icon and growing downwards.
            var top = Math.Min(icon.Y, iconScreen.VisibleFrame.GetMaxY()) - MenuBarGap;
            _reference = new CGRect(icon.GetMidX(), top, 0, 0);
            _horizontal = Anchor.Center;
            _vertical = Anchor.End;
            return;
        }

        var screen = ScreenFor(new CGRect(NSEvent.CurrentMouseLocation, CGSize.Empty)) ?? NSScreen.MainScreen;
        var area = screen?.VisibleFrame ?? new CGRect(0, 0, 1440, 900);
        _reference = area.Inset((nfloat)ScreenMargin, (nfloat)ScreenMargin);
        (_horizontal, _vertical) = position switch
        {
            VoiceBubblePosition.BottomLeft => (Anchor.Start, Anchor.Start),
            VoiceBubblePosition.TopLeft => (Anchor.Start, Anchor.End),
            VoiceBubblePosition.Center => (Anchor.Center, Anchor.Center),
            VoiceBubblePosition.BottomRight => (Anchor.End, Anchor.Start),
            _ => (Anchor.End, Anchor.End)
        };
    }

    /// <summary>The panel frame for <paramref name="size"/> at the anchor, kept inside the screen.</summary>
    private CGRect FrameFor(CGSize size)
    {
        double x = _horizontal switch
        {
            Anchor.Start => _reference.X,
            Anchor.Center => _reference.GetMidX() - (size.Width / 2),
            _ => _reference.GetMaxX() - size.Width
        };
        double y = _vertical switch
        {
            Anchor.Start => _reference.Y,
            Anchor.Center => _reference.GetMidY() - (size.Height / 2),
            _ => _reference.GetMaxY() - size.Height
        };

        if (ScreenFor(_reference) is { } screen)
        {
            var area = screen.VisibleFrame;
            x = Math.Clamp(x, area.X + ScreenMargin, Math.Max(area.X + ScreenMargin, area.GetMaxX() - size.Width - ScreenMargin));
            y = Math.Clamp(y, area.Y + ScreenMargin, Math.Max(area.Y + ScreenMargin, area.GetMaxY() - size.Height));
        }

        return new CGRect(Math.Round(x), Math.Round(y), size.Width, size.Height);
    }

    private static NSScreen? ScreenFor(CGRect rect)
    {
        var point = new CGPoint(rect.GetMidX(), rect.GetMidY());
        return NSScreen.Screens.FirstOrDefault(screen => screen.Frame.Contains(point))
               ?? NSScreen.Screens.FirstOrDefault(screen => screen.Frame.IntersectsWith(rect));
    }

    /// <summary>
    /// Floats above other apps and full-screen spaces and takes the keyboard without activating Wino; its shadow
    /// follows the bubble's rounded material.
    /// </summary>
    private sealed class VoiceBubblePanel : NSPanel
    {
        public VoiceBubblePanel(WinoVoiceBubbleView bubble)
            : base(new CGRect(0, 0, 44, 44), NSWindowStyle.Borderless | NSWindowStyle.NonactivatingPanel, NSBackingStore.Buffered, false)
        {
            Level = NSWindowLevel.Floating;
            CollectionBehavior = NSWindowCollectionBehavior.CanJoinAllSpaces | NSWindowCollectionBehavior.FullScreenAuxiliary | NSWindowCollectionBehavior.IgnoresCycle;
            BackgroundColor = NSColor.Clear;
            IsOpaque = false;
            HasShadow = true;
            HidesOnDeactivate = false;
            FloatingPanel = true;
            BecomesKeyOnlyIfNeeded = false;
            WorksWhenModal = true;
            AnimationBehavior = NSWindowAnimationBehavior.None;
            bubble.Frame = new CGRect(0, 0, 44, 44);
            bubble.AutoresizingMask = NSViewResizingMask.WidthSizable | NSViewResizingMask.HeightSizable;
            ContentView = bubble;
            AlphaValue = 0;
            ReleaseWhenClosed(false);
        }

        public override bool CanBecomeKeyWindow => true;

        public override bool CanBecomeMainWindow => false;
    }
}
