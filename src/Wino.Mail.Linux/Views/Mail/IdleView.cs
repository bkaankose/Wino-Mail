using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Messaging.Client.Shell;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Mail;

/// <summary>
/// The reading pane while no message is open (macOS <c>IdlePageViewController</c>): "No message selected" with the
/// user's Reply and Delete shortcuts, an account that needs a sign-in (or is loading its first folders), and no account
/// at all. The pane keeps this view, so a later navigation renavigates it with the new state.
/// </summary>
internal sealed class IdleView : WinoView<IdlePageViewModel>, IRenavigableView
{
    private readonly MailIdleContent _content = new();
    private readonly IKeyboardShortcutService _shortcuts;
    private bool _empty;
    private bool _signIn;

    public IdleView(IdlePageViewModel viewModel, WinoViewServices services, IKeyboardShortcutService shortcuts) : base(viewModel, services)
    {
        _shortcuts = shortcuts;
        Bindings.Own(_content);
        _content.ActionInvoked += () =>
        {
            if (_empty) ViewModel.AddAccountCommand.Execute(null);
            else if (_signIn) Observe(ViewModel.SignInCommand.ExecuteAsync(null));
        };
        _content.SecondaryInvoked += () =>
        {
            if (_empty) ViewModel.ManageAccountsCommand.Execute(null);
        };
        Bind.BindAny(static vm => (vm.IsMailEmptyStateVisible, vm.IsAccountStateVisible, vm.AccountStateTitle, vm.AccountStateMessage, vm.IsSignInVisible), _ => Apply());
        Subscribe<LanguageChanged>(_ => Apply());
        EventHandler changed = (_, _) => RunOnUI(Apply);
        shortcuts.KeyboardShortcutsChanged += changed;
        Bindings.Own(new ActionDisposable(() => shortcuts.KeyboardShortcutsChanged -= changed));
        Apply();
    }

    public override Gtk.Widget Root => _content.Root;

    public Task RenavigateAsync(object? parameter)
    {
        ViewModel.OnNavigatedTo(Core.Domain.Models.Navigation.NavigationMode.New, parameter);
        return Task.CompletedTask;
    }

    /// <summary>One line per state, for checks.</summary>
    internal string Describe() => _content.Describe();

    private void Apply()
    {
        var account = ViewModel.IsAccountStateVisible;
        _empty = !account && ViewModel.IsMailEmptyStateVisible;
        _signIn = account && ViewModel.IsSignInVisible;
        if (account)
        {
            _content.SetContent(ViewModel.AccountStateTitle, ViewModel.AccountStateMessage, _signIn ? ViewModel.SignInText : null, loading: !_signIn);
            _content.SetHints([]);
        }
        else if (_empty)
        {
            _content.SetContent(ViewModel.MailEmptyStateTitle, ViewModel.MailEmptyStateMessage, ViewModel.AddAccountText, ViewModel.ManageAccountsText);
            _content.SetHints([]);
        }
        else
        {
            _content.SetContent(Translator.NoMailSelected, null, null);
            _content.SetHints(Hints());
        }
    }

    private (string Accelerator, string Title)[] Hints()
    {
        var hints = new List<(string, string)>(2);
        foreach (var (action, title) in new[] { (KeyboardShortcutAction.Reply, Translator.MailOperation_Reply), (KeyboardShortcutAction.Delete, Translator.MailOperation_Delete) })
        {
            var shortcut = _shortcuts.EnabledShortcutsSnapshot.FirstOrDefault(candidate => candidate.Mode == WinoApplicationMode.Mail && candidate.Action == action);
            if (shortcut is not null && ShortcutKeys.Accelerator(shortcut.ModifierKeys, shortcut.Key) is { } accelerator) hints.Add((accelerator, title));
        }
        return [.. hints];
    }
}
