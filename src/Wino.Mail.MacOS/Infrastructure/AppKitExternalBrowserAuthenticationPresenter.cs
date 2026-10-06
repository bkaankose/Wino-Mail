using AppKit;
using Foundation;
using CoreGraphics;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>Nonmodal, owner-tracked OAuth progress never takes the account/dialog gate.</summary>
public sealed class AppKitExternalBrowserAuthenticationPresenter(IDispatcher dispatcher, Func<NSWindow?> owner, Action<Exception> error) : IExternalBrowserAuthenticationPresenter
{
    public async Task<IExternalBrowserAuthenticationSession> ShowAsync(ExternalBrowserAuthenticationRequest request, Action cancelRequested)
    {
        Session? session = null;
        await dispatcher.ExecuteOnUIThread(() => session = new Session(dispatcher, owner() ?? throw new InvalidOperationException("No authentication owner is available."), request, cancelRequested, error));
        return session!;
    }
    private sealed class Session : IExternalBrowserAuthenticationSession
    {
        private readonly IDispatcher _dispatcher;
        private readonly NSWindow _owner;
        private readonly NSWindow _window;
        private readonly NSTextField _message;
        private readonly NSButton _copy;
        private readonly NSButton _cancel;
        private readonly Action _cancelRequested;
        private readonly Action<Exception> _error;
        private readonly Uri _authorizationUri;
        private NSObject? _ownerClosing;
        private NSObject? _windowClosing;
        private int _disposed;
        private int _canceled;
        public Session(IDispatcher dispatcher, NSWindow owner, ExternalBrowserAuthenticationRequest request, Action cancelRequested, Action<Exception> error)
        {
            _dispatcher = dispatcher; _owner = owner; _cancelRequested = cancelRequested; _error = error; _authorizationUri = request.AuthorizationUri;
            _window = new NSWindow(new CGRect(0, 0, 480, 210), NSWindowStyle.Titled | NSWindowStyle.Closable, NSBackingStore.Buffered, false) { Title = "Sign in to " + request.ProviderDisplayName, ReleasedWhenClosed = false };
            _message = new NSTextField { StringValue = string.Format(Translator.ExternalBrowserAuthenticationDialog_Message, request.ProviderDisplayName), Editable = false, Selectable = false, Bordered = false, DrawsBackground = false };
            _copy = new NSButton { Title = "Copy sign-in link" };
            _cancel = new NSButton { Title = Translator.Cancel };
            _copy.Activated += CopyLink;
            _cancel.Activated += Cancel;
            var commands = Layout.Stack(NSUserInterfaceLayoutOrientation.Horizontal, _copy, _cancel);
            var content = Layout.Stack(NSUserInterfaceLayoutOrientation.Vertical, _message, commands);
            Layout.Fill(content, _window.ContentView!, 20);
            _ownerClosing = NSNotificationCenter.DefaultCenter.AddObserver(NSWindow.WillCloseNotification, _ => Cancel(this, EventArgs.Empty), owner);
            _windowClosing = NSNotificationCenter.DefaultCenter.AddObserver(NSWindow.WillCloseNotification, _ => { if (_disposed == 0) Cancel(this, EventArgs.Empty); }, _window);
            _window.Center(); _window.MakeKeyAndOrderFront(null);
        }
        private void CopyLink(object? sender, EventArgs args)
        {
            if (_disposed != 0) return;
            NSPasteboard.GeneralPasteboard.ClearContents();
            NSPasteboard.GeneralPasteboard.SetStringForType(_authorizationUri.AbsoluteUri, NSPasteboard.NSPasteboardTypeString);
        }
        private void Cancel(object? sender, EventArgs args)
        {
            if (_disposed != 0 || Interlocked.Exchange(ref _canceled, 1) != 0) return;
            try { _cancelRequested(); }
            catch (Exception exception) { _error(exception); }
        }
        public Task NotifyBrowserLaunchFailedAsync() => _dispatcher.ExecuteOnUIThread(() =>
        {
            if (_disposed == 0) _message.StringValue = "The browser could not open. Copy the sign-in link and paste it into your browser.";
        });
        public Task NotifyRedirectReceivedAsync() => _dispatcher.ExecuteOnUIThread(() =>
        {
            if (_disposed != 0) return;
            NSApplication.SharedApplication.ActivateIgnoringOtherApps(true);
            _owner.MakeKeyAndOrderFront(null);
        });
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            await _dispatcher.ExecuteOnUIThread(() =>
            {
                if (_ownerClosing is not null) NSNotificationCenter.DefaultCenter.RemoveObserver(_ownerClosing);
                if (_windowClosing is not null) NSNotificationCenter.DefaultCenter.RemoveObserver(_windowClosing);
                _ownerClosing?.Dispose(); _windowClosing?.Dispose();
                _copy.Activated -= CopyLink; _cancel.Activated -= Cancel;
                _window.Close(); _window.Dispose();
            });
        }
    }
}
