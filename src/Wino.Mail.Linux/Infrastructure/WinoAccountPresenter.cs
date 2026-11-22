using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Linux.Views.Shell;
using Wino.Core.Domain.Messaging.UI;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Infrastructure;

/// <summary>
/// Shows the Wino Account popover under the header button (macOS WinoAccountPresenter, Windows WinoAccountFlyout).
/// Opening refreshes the profile; sign-in and registration go through the dialog service; Manage opens Settings ›
/// Wino Account. It also tells the shell when the signed-in account changes so the header shows its picture.
/// </summary>
internal sealed class WinoAccountPresenter : IWinoAccountPresenter,
    IRecipient<WinoAccountSignedInMessage>, IRecipient<WinoAccountSignedOutMessage>,
    IRecipient<WinoAccountProfileUpdatedMessage>, IRecipient<WinoAccountProfileDeletedMessage>
{
    private readonly IWinoAccountProfileService _profiles;
    private readonly IMailDialogService _dialogs;
    private readonly ISettingsWindowPresenter _settings;
    private readonly IDispatcher _dispatcher;
    private WinoAccountPopover? _popover;
    private int _version;

    public WinoAccountPresenter(IWinoAccountProfileService profiles, IMailDialogService dialogs, ISettingsWindowPresenter settings,
        IDispatcher dispatcher, IMessenger messenger)
    {
        _profiles = profiles;
        _dialogs = dialogs;
        _settings = settings;
        _dispatcher = dispatcher;
        messenger.Register<WinoAccountSignedInMessage>(this);
        messenger.Register<WinoAccountSignedOutMessage>(this);
        messenger.Register<WinoAccountProfileUpdatedMessage>(this);
        messenger.Register<WinoAccountProfileDeletedMessage>(this);
    }

    /// <summary>Raised on the main thread when the signed-in account or its profile changed.</summary>
    public event Action? Changed;

    /// <summary>The signed-in account, or null when signed out or when the session needs a new sign-in.</summary>
    public async Task<WinoAccount?> GetActiveAccountAsync()
        => await _profiles.GetActiveAccountAsync().ConfigureAwait(false) is { SessionInvalidatedUtc: null } account ? account : null;

    /// <summary>The account's picture file decoded on the calling thread; null without one.</summary>
    public async Task<Gdk.Texture?> GetAvatarAsync(WinoAccount account)
    {
        try
        {
            var path = await _profiles.GetAvatarPathAsync(account.Id, account.AvatarRevision).ConfigureAwait(false);
            var bytes = await Textures.ReadAsync(path).ConfigureAwait(false);
            Gdk.Texture? texture = null;
            await _dispatcher.ExecuteOnUIThread(() => texture = Textures.FromBytes(bytes)).ConfigureAwait(false);
            return texture;
        }
        catch (Exception exception)
        {
            Serilog.Log.Warning(exception, "The Wino Account picture could not be read.");
            return null;
        }
    }

    public void Show(Gtk.Widget anchor)
    {
        if (_popover is { } open && open.Root.GetVisible())
        {
            open.Root.Popdown();
            return;
        }
        var popover = _popover ??= CreatePopover();
        if (!ReferenceEquals(popover.Root.GetParent(), anchor))
        {
            if (popover.Root.GetParent() is not null) popover.Root.Unparent();
            popover.Root.SetParent(anchor);
        }
        popover.Root.Popup();
        Run(RefreshAsync(popover, ++_version));
    }

    public void Receive(WinoAccountSignedInMessage message) => RaiseChanged();

    public void Receive(WinoAccountSignedOutMessage message) => RaiseChanged();

    public void Receive(WinoAccountProfileUpdatedMessage message) => RaiseChanged();

    public void Receive(WinoAccountProfileDeletedMessage message) => RaiseChanged();

#if DEBUG
    internal WinoAccountPopover? Popover => _popover;
#endif

    private void RaiseChanged() => _ = _dispatcher.ExecuteOnUIThread(() => Changed?.Invoke());

    private WinoAccountPopover CreatePopover()
    {
        var popover = new WinoAccountPopover();
        popover.SignInRequested += () => Run(SignInAsync());
        popover.RegisterRequested += () => Run(RegisterAsync());
        popover.ManageRequested += () => Run(_settings.ShowAsync(WinoPage.WinoAccountManagementPage, null));
        popover.SignOutRequested += () => Run(SignOutAsync());
        return popover;
    }

    /// <summary>
    /// The shared management page's rule: a profile refresh that needs a new sign-in shows the popover signed out,
    /// although the stored account row stays until that sign-in replaces it.
    /// </summary>
    private static bool NeedsSignIn(string? errorCode)
        => errorCode is Wino.Mail.Api.Contracts.Common.ApiErrorCodes.RefreshTokenInvalid
            or WinoAccountClientErrorCodes.SignInRequired
            or WinoAccountClientErrorCodes.AccountSessionChanged
            or WinoAccountClientErrorCodes.RefreshTokenReused
            or WinoAccountClientErrorCodes.SessionInvalid;

    private async Task RefreshAsync(WinoAccountPopover popover, int version)
    {
        var cached = await GetActiveAccountAsync().ConfigureAwait(false);
        await _dispatcher.ExecuteOnUIThread(() =>
        {
            if (version != _version) return;
            if (cached is null) popover.ShowSignedOut();
            else popover.ShowSignedIn(cached.DisplayName, cached.Email, null);
            popover.SetBusy(cached is not null);
        }).ConfigureAwait(false);

        var needsSignIn = false;
        if (cached is not null)
        {
            try { needsSignIn = await _profiles.RefreshProfileAsync().ConfigureAwait(false) is { IsSuccess: false } failure && NeedsSignIn(failure.ErrorCode); }
            catch (Exception exception) { Serilog.Log.Warning(exception, "The Wino Account profile could not be refreshed."); }
        }
        var account = needsSignIn ? null : await GetActiveAccountAsync().ConfigureAwait(false);
        var avatar = account is null ? null : await GetAvatarAsync(account).ConfigureAwait(false);
        await _dispatcher.ExecuteOnUIThread(() =>
        {
            if (version != _version) return;
            popover.SetBusy(false);
            if (account is null) popover.ShowSignedOut();
            else popover.ShowSignedIn(account.DisplayName, account.Email, avatar);
        }).ConfigureAwait(false);
    }

    private async Task SignInAsync()
    {
        var account = await _dialogs.ShowWinoAccountLoginDialogAsync();
        if (account is not null) Info(string.Format(Translator.WinoAccount_LoginSuccessMessage, account.Email), InfoBarMessageType.Success);
    }

    private async Task RegisterAsync()
    {
        var account = await _dialogs.ShowWinoAccountRegistrationDialogAsync();
        if (account is not null) Info(string.Format(Translator.WinoAccount_RegisterSuccessMessage, account.Email), InfoBarMessageType.Success);
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
        Info(string.Format(Translator.WinoAccount_SignOut_SuccessMessage, account.Email), InfoBarMessageType.Success);
    }

    private void Info(string message, InfoBarMessageType type)
        => _ = _dispatcher.ExecuteOnUIThread(() => _dialogs.InfoBarMessage(Translator.GeneralTitle_Info, message, type));

    private static async void Run(Task task)
    {
        try { await task; }
        catch (Exception exception) { Serilog.Log.Error(exception, "Wino Account action failed."); }
    }
}
