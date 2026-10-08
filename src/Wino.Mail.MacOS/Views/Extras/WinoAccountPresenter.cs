using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.MacOS.Infrastructure;

namespace Wino.Mail.MacOS.Views.Extras;

/// <summary>
/// Shows the Wino Account popover under the title-bar account button (Windows WinoAccountFlyout).
/// Opening refreshes the profile; sign in and registration go through the shared dialog service
/// (which reports when they are not available on Mac yet); sign out and Manage behave as on Windows.
/// </summary>
public sealed class WinoAccountPresenter : IWinoAccountPresenter
{
    private readonly IWinoAccountProfileService _profiles;
    private readonly IMailDialogService _dialogs;
    private readonly AppKitNavigationService _navigation;
    private readonly IDispatcher _dispatcher;
    private readonly IWinoLogger _logger;
    private NSPopover? _popover;
    private int _version;

    public WinoAccountPresenter(IWinoAccountProfileService profiles, IMailDialogService dialogs, AppKitNavigationService navigation,
        IDispatcher dispatcher, IWinoLogger logger, IServiceProvider services)
    {
        _profiles = profiles;
        _dialogs = dialogs;
        _navigation = navigation;
        _dispatcher = dispatcher;
        _logger = logger;
#if DEBUG
        ShellExtrasDebug.Services ??= services;
#endif
    }

    /// <summary>The signed-in account, for the shell's button avatar; null when signed out.</summary>
    public Task<WinoAccount?> GetActiveAccountAsync() => _profiles.GetActiveAccountAsync();

    public void Show(NSView anchor)
    {
        if (_popover is { Shown: true })
        {
            _popover.Close();
            return;
        }
        var content = new WinoAccountPopoverViewController();
        content.SignInRequested += (_, _) => Run(SignInAsync());
        content.RegisterRequested += (_, _) => Run(RegisterAsync());
        content.ManageRequested += (_, _) => { Close(); _navigation.Navigate(WinoPage.WinoAccountManagementPage); };
        content.SignOutRequested += (_, _) => Run(SignOutAsync());
        var popover = new NSPopover { ContentViewController = content, Behavior = NSPopoverBehavior.Transient, Animates = true };
        popover.Delegate = new PopoverDelegate(() => { if (ReferenceEquals(_popover, popover)) _popover = null; });
        _popover = popover;
        popover.Show(anchor.Bounds, anchor, NSRectEdge.MinYEdge);
        Run(RefreshAsync(content, ++_version));
    }

    private void Close() => _popover?.Close();

    private async Task RefreshAsync(WinoAccountPopoverViewController content, int version)
    {
        try { await _profiles.RefreshProfileAsync().ConfigureAwait(false); }
        catch (Exception exception) { _logger.CaptureException(exception, nameof(WinoAccountPresenter)); }
        var account = await _profiles.GetActiveAccountAsync().ConfigureAwait(false);
        NSImage? avatar = null;
        if (account is not null)
        {
            try
            {
                var path = await _profiles.GetAvatarPathAsync(account.Id, account.AvatarRevision).ConfigureAwait(false);
                if (path is not null && File.Exists(path)) avatar = new NSImage(path);
            }
            catch (Exception exception) { _logger.CaptureException(exception, nameof(WinoAccountPresenter)); }
        }
        await _dispatcher.ExecuteOnUIThread(() => { if (version == _version) content.Update(account, avatar); }).ConfigureAwait(false);
    }

    private async Task SignInAsync()
    {
        await _dispatcher.ExecuteOnUIThread(Close);
        var account = await _dialogs.ShowWinoAccountLoginDialogAsync();
        if (account is not null)
            Info(string.Format(Translator.WinoAccount_LoginSuccessMessage, account.Email), InfoBarMessageType.Success);
    }

    private async Task RegisterAsync()
    {
        await _dispatcher.ExecuteOnUIThread(Close);
        var account = await _dialogs.ShowWinoAccountRegistrationDialogAsync();
        if (account is not null)
            Info(string.Format(Translator.WinoAccount_RegisterSuccessMessage, account.Email), InfoBarMessageType.Success);
    }

    private async Task SignOutAsync()
    {
        var account = await _profiles.GetActiveAccountAsync();
        if (account is null)
        {
            Info(Translator.WinoAccount_SignOut_NoAccountMessage, InfoBarMessageType.Warning);
            return;
        }
        await _profiles.SignOutAsync();
        await _dispatcher.ExecuteOnUIThread(Close);
        Info(string.Format(Translator.WinoAccount_SignOut_SuccessMessage, account.Email), InfoBarMessageType.Success);
    }

    private void Info(string message, InfoBarMessageType type)
        => _ = _dispatcher.ExecuteOnUIThread(() => _dialogs.InfoBarMessage(Translator.GeneralTitle_Info, message, type));

    private async void Run(Task task)
    {
        try { await task; }
        catch (Exception exception) { _logger.CaptureException(exception, nameof(WinoAccountPresenter)); }
    }

    private sealed class PopoverDelegate(Action closed) : NSPopoverDelegate
    {
        public override void DidClose(Foundation.NSNotification notification) => closed();
    }
}
