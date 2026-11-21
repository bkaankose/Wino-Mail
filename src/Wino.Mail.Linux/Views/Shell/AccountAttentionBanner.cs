using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Shell;

/// <summary>
/// A persistent banner under the shell header while an account needs attention (the Windows shell info bar; a toast
/// would disappear): "Sign in to …" with Sign in for an account that signing in fixes, else "'…' requires your
/// attention" with Fix. The button runs the shared fix of the sidebar's attention row. It is revealed only after the
/// first refresh, so a stale state never flashes at start. Main thread.
/// </summary>
internal sealed class AccountAttentionBanner : IDisposable
{
    private readonly Adw.Banner _banner = Adw.Banner.New(string.Empty);
    private readonly IAccountService _accounts;
    private readonly Func<MailAccount, Task> _fix;
    private readonly Action<Exception> _error;
    private readonly MainLoop.Coalescer _refresh;
    private readonly IDisposable _clicked;
    private MailAccount? _account;
    private int _version;
    private bool _disposed;

    public AccountAttentionBanner(IAccountService accounts, Func<MailAccount, Task> fix, Action<Exception> error)
    {
        _accounts = accounts;
        _fix = fix;
        _error = error;
        _banner.SetUseMarkup(false);
        _banner.SetRevealed(false);
        _refresh = new MainLoop.Coalescer(() => _ = RefreshAsync());
        _clicked = GtkSignals.Connect<Adw.Banner>(h => _banner.OnButtonClicked += h, h => _banner.OnButtonClicked -= h, (_, _) => Invoke());
    }

    public Gtk.Widget Root => _banner;

    public bool IsRevealed => _banner.GetRevealed();

    public MailAccount? Account => _account;

    public string Describe() => $"revealed={_banner.GetRevealed()} title={_banner.GetTitle()} button={_banner.GetButtonLabel()}";

    /// <summary>Asks for one refresh on the next main-loop turn; bursts of account messages coalesce.</summary>
    public void Invalidate() => _refresh.Request();

    public async Task RefreshAsync()
    {
        if (_disposed) return;
        var version = ++_version;
        List<MailAccount> accounts;
        try { accounts = await _accounts.GetAccountsAsync(); }
        catch (Exception exception)
        {
            _error(exception);
            return;
        }
        if (_disposed || version != _version) return;
        Apply(accounts.FirstOrDefault(account => account.RequiresAttention(WinoApplicationMode.Mail)));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refresh.Dispose();
        _clicked.Dispose();
    }

    private void Apply(MailAccount? account)
    {
        _account = account;
        if (account is null)
        {
            _banner.SetRevealed(false);
            return;
        }
        var name = string.IsNullOrWhiteSpace(account.Name) ? account.Address : account.Name;
        var signIn = account.CanBeFixedBySigningIn();
        _banner.SetTitle(signIn
            ? string.Format(Translator.MailAccountIdle_SignInTitle, name)
            : string.Format(Translator.Exception_AccountNeedsAttention_Message, name));
        _banner.SetButtonLabel(signIn ? Translator.MailAccountIdle_SignIn : Translator.Info_AccountAttentionRequiredAction);
        _banner.SetRevealed(true);
    }

    private void Invoke()
    {
        if (_account is not { } account) return;
        _ = RunAsync(account);
    }

    private async Task RunAsync(MailAccount account)
    {
        try { await _fix(account); }
        catch (Exception exception) { _error(exception); }
    }
}
