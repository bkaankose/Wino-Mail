using AppKit;
using Foundation;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>
/// Shows ViewModel info-bar messages inline, like the Windows ShellInfoBar: one WinoInfoBar at the top
/// right of the window's content (max 700pt wide, 25pt from the edge), with no close button
/// (IsClosable="False") and dismissed after five seconds (WinoInfoBar.DismissInterval default), later
/// messages queued behind it. The countdown pauses while the pointer is over the bar. Works for any
/// titled window (main window, Settings window).
/// </summary>
internal static class InlineInfoBarPresenter
{
    private static readonly TimeSpan DismissInterval = TimeSpan.FromSeconds(5);
    private static readonly Dictionary<nint, Host> Hosts = [];

    /// <summary>Returns false when the window cannot host an inline bar; the caller falls back to an alert.</summary>
    public static bool TryShow(NSWindow? window, string title, string message, InfoBarMessageType type, string? actionTitle = null, Action? action = null)
    {
        if (window?.ContentView is not { } content || !window.StyleMask.HasFlag(NSWindowStyle.Titled) || content.Bounds.Width < 400) return false;
        if (!Hosts.TryGetValue(window.Handle, out var host))
        {
            host = new Host(window);
            Hosts[window.Handle] = host;
            // Observe the notification: windows with their own delegate reject the WillClose event.
            NSObject? observer = null;
            observer = NSNotificationCenter.DefaultCenter.AddObserver(NSWindow.WillCloseNotification, _ =>
            {
                Hosts.Remove(window.Handle);
                host.Dispose();
                if (observer is not null) NSNotificationCenter.DefaultCenter.RemoveObserver(observer);
            }, window);
        }
        host.Enqueue(new Message(title, message, type, actionTitle, action));
        return true;
    }

    private sealed record Message(string Title, string Text, InfoBarMessageType Type, string? ActionTitle, Action? Action);

    private sealed class Host : IDisposable
    {
        private readonly Queue<Message> _pending = new();
        private readonly WinoSurfaceView _container;
        private readonly WinoInfoBar _bar;
        private Action? _action;

        public Host(NSWindow window)
        {
            var content = window.ContentView!;
            _bar = new WinoInfoBar { TranslatesAutoresizingMaskIntoConstraints = false, IsClosable = false, AutoDismissInterval = DismissInterval };
            _bar.ActionInvoked += (_, _) => { var action = _action; Close(); action?.Invoke(); };
            // The bar hides itself when its timer elapses; the host hides the container instead and unhides the
            // bar inside it, so the bar is ready (and restarts its countdown) when the next message shows.
            _bar.Closed += (_, _) => { Close(); _bar.Hidden = false; };
            // The bar's own tint is translucent; an opaque base keeps it readable over mail content.
            _container = new WinoSurfaceView { Fill = NSColor.WindowBackground, CornerRadius = WinoStyle.GroupRadius, Hidden = true };
            _container.WantsLayer = true;
            _container.Shadow = new NSShadow { ShadowColor = NSColor.Black.ColorWithAlphaComponent(0.22f), ShadowBlurRadius = 14, ShadowOffset = new CoreGraphics.CGSize(0, -4) };
            WinoLayout.Fill(_bar, _container);
            content.AddSubview(_container, NSWindowOrderingMode.Above, null);
            NSLayoutConstraint.ActivateConstraints(
            [
                _container.TopAnchor.ConstraintEqualTo(content.SafeAreaLayoutGuide.TopAnchor, 12),
                _container.TrailingAnchor.ConstraintEqualTo(content.TrailingAnchor, -25),
                _container.WidthAnchor.ConstraintLessThanOrEqualTo(700),
                _container.WidthAnchor.ConstraintGreaterThanOrEqualTo(320),
                _container.LeadingAnchor.ConstraintGreaterThanOrEqualTo(content.LeadingAnchor, 25)
            ]);
        }

        public void Enqueue(Message message)
        {
            if (!_container.Hidden) { _pending.Enqueue(message); return; }
            Show(message);
        }

        private void Show(Message message)
        {
            _bar.Title = message.Title;
            _bar.Message = message.Text;
            _bar.Severity = message.Type switch
            {
                InfoBarMessageType.Success => WinoInfoBarSeverity.Success,
                InfoBarMessageType.Warning => WinoInfoBarSeverity.Warning,
                InfoBarMessageType.Error => WinoInfoBarSeverity.Error,
                _ => WinoInfoBarSeverity.Informational
            };
            _action = message.Action;
            _bar.ActionTitle = message.Action is null ? null : message.ActionTitle;
            // Keep it on top of views added after the host was created.
            var parent = _container.Superview;
            _container.RemoveFromSuperview();
            parent?.AddSubview(_container, NSWindowOrderingMode.Above, null);
            ReattachConstraints(parent);
            _container.Hidden = false;
            NSAccessibility.PostNotification(_bar, new NSString("AXAnnouncementRequested"),
                NSDictionary.FromObjectAndKey(new NSString($"{message.Title} {message.Text}"), NSAccessibilityNotificationUserInfoKeys.AnnouncementKey));
            // Unhiding the container starts the bar's countdown; an identical repeat message restarts it here.
            _bar.RestartAutoDismiss();
        }

        private void ReattachConstraints(NSView? content)
        {
            if (content is null) return;
            NSLayoutConstraint.ActivateConstraints(
            [
                _container.TopAnchor.ConstraintEqualTo(content.SafeAreaLayoutGuide.TopAnchor, 12),
                _container.TrailingAnchor.ConstraintEqualTo(content.TrailingAnchor, -25),
                _container.LeadingAnchor.ConstraintGreaterThanOrEqualTo(content.LeadingAnchor, 25)
            ]);
        }

        private void Close()
        {
            _action = null;
            _container.Hidden = true;
            if (_pending.TryDequeue(out var next)) Show(next);
        }

        public void Dispose()
        {
            _pending.Clear();
            // Hiding stops the bar's countdown before the window goes away.
            _container.Hidden = true;
        }
    }
}
