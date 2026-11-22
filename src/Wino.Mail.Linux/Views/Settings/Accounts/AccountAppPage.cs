using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.Linux.Common;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels.AccountSettings;
using Wino.Presentation.Linux;
#if DEBUG
using System.Text;
#endif

namespace Wino.Mail.Linux.Views.Settings.Accounts;

/// <summary>
/// The page of one app of an account (Windows AccountAppHeaderControl plus the app's settings, macOS
/// AccountAppPageViewController): the sign-in bar, the switch for the app, "Syncs with" as two cards (the server or this
/// device) or a text row, the CalDAV/CardDAV server row, the note while the app is off, then the app's own settings.
/// The navigation parameter is the account id.
/// </summary>
internal abstract class AccountAppPage<TViewModel>(TViewModel viewModel, WinoViewServices services)
    : SettingsPageView<TViewModel>(viewModel, services), ISettingsPageTitleSource
#if DEBUG
    , IAccountAppPageDebug
#endif
    where TViewModel : AccountAppPageViewModelBase
{
    private readonly List<(AccountAppSourceOption Option, WinoSourceChoiceCard Card)> _cards = [];
    private Gtk.Box? _cardBox;
    private Adw.ActionRow? _serverRow;

    public string? PageTitle => ViewModel.AppItem.Title;

    public event EventHandler? PageTitleChanged;

    protected sealed override void BuildPage()
    {
        var vm = ViewModel;
#if DEBUG
        IAccountAppPageDebug.Current = this;
        Settings.Own(new ActionDisposable(() =>
        {
            if (ReferenceEquals(IAccountAppPageDebug.Current, this)) IAccountAppPageDebug.Current = null;
        }));
#endif
        Settings.Bind(vm, nameof(vm.Account), static s => s.Account, _ => PageTitleChanged?.Invoke(this, EventArgs.Empty));

        var signIn = new WinoInfoBar(WinoInfoBarSeverity.Warning, null, string.Format(Translator.AccountApps_SignInNeededMessage, vm.AppItem.Title))
        {
            IsClosable = false,
            ActionTitle = Translator.AccountApps_SignIn
        };
        Settings.Own(signIn);
        signIn.ActionInvoked += _ =>
        {
            if (vm.SignInAgainCommand.CanExecute(null)) vm.SignInAgainCommand.Execute(null);
        };
        var bars = NonRowGroup(signIn.Root);
        Settings.Bind(vm, nameof(vm.NeedsSignIn), static s => s.NeedsSignIn, needed =>
        {
            signIn.IsOpen = needed;
            bars.Root.SetVisible(needed);
        });

        var item = vm.AppItem;
        var toggleRow = WinoSettingsRows.Action(item.UseTitle, item.UseDescription, item.Icon);
        var busy = WinoA11y.Label(Adw.Spinner.New(), Translator.ExchangeSetup_Checking);
        Settings.Visible(WinoSettingsRows.Suffix(toggleRow, busy), item, nameof(item.IsBusy), static s => s.IsBusy);
        var toggle = WinoSettingsRows.Suffix(toggleRow, Settings.SwitchSuffix(item, item.UseTitle, nameof(item.IsOn), static s => s.IsOn, static (s, v) => s.IsOn = v));
        Settings.Enabled(toggle, item, nameof(item.CanToggle), static s => s.CanToggle);
        Settings.Bind(item, nameof(item.ToggleHint), static s => s.ToggleHint, hint => WinoA11y.Description(toggle, hint));
        toggleRow.SetActivatableWidget(toggle);
        var toggleGroup = Page.AddGroup(null, null, toggleRow);
        var lastApp = WinoSettingsRows.Caption(Translator.AccountApps_LastAppHint);
        lastApp.SetMarginTop(6);
        Settings.Visible(lastApp, item, nameof(item.IsLastApp), static s => s.IsLastApp);
        toggleGroup.Root.Add(lastApp);

        BuildSyncsWith();

        var off = WinoSettingsRows.Caption(vm.OffNote);
        var offGroup = NonRowGroup(off);
        Settings.Visible(offGroup.Root, vm, nameof(vm.IsOff), static s => s.IsOff);

        BuildAppSettings();
    }

    /// <summary>The app's own settings below "Syncs with", in the macOS order.</summary>
    protected abstract void BuildAppSettings();

    protected override Task NavigateAsync(NavigationMode mode, object? parameter) => ViewModel.InitializeAsync(mode, parameter);

    /// <summary>A group whose only content sits outside the boxed list (an info bar, a caption).</summary>
    protected WinoSettingsGroup NonRowGroup(Gtk.Widget content)
    {
        var group = Page.AddGroup();
        group.HidesWhenEmpty = false;
        group.Root.Add(content);
        return group;
    }

    /// <summary>A button labelled for assistive technology (a labelled button is otherwise named by its label relation).</summary>
    protected Gtk.Button Named(string title, System.Windows.Input.ICommand command, bool suggested = false)
        => WinoA11y.Rename(Settings.Button(title, command, suggested: suggested), title);

    private void BuildSyncsWith()
    {
        var vm = ViewModel;
        var cardBox = _cardBox = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
        cardBox.SetHomogeneous(true);
        cardBox.SetVexpand(false);
        Settings.Visible(cardBox, vm, nameof(vm.IsSourceChoiceVisible), static s => s.IsSourceChoiceVisible);
        var choice = NonRowGroup(cardBox);
        choice.Title = Translator.AccountApps_SyncsWith;
        var changing = WinoA11y.Label(Adw.Spinner.New(), Translator.ExchangeSetup_Checking);
        Settings.Visible(changing, vm, nameof(vm.IsChangingSource), static s => s.IsChangingSource);
        choice.HeaderSuffix = changing;
        Settings.Visible(choice.Root, vm, nameof(vm.IsSourceChoiceVisible), static s => s.IsSourceChoiceVisible);

        Settings.Collection(vm.SourceOptions, RebuildCards);
        Settings.Bind(vm, nameof(vm.SelectedSourceOption), static s => s.SelectedSourceOption, SelectCards);
        Settings.Bind(vm, nameof(vm.IsChangingSource), static s => s.IsChangingSource, changingNow =>
        {
            foreach (var (_, card) in _cards) card.IsEnabled = !changingNow;
        });

        var text = WinoSettingsRows.Action(vm.SourceText, vm.SourceDescription, WinoIconGlyph.Sync);
        Settings.Bind(vm, nameof(vm.SourceText), static s => s.SourceText, value => text.SetTitle(value ?? string.Empty));
        Settings.Subtitle(text, vm, nameof(vm.SourceDescription), static s => s.SourceDescription);
        Settings.Visible(text, vm, nameof(vm.IsSourceTextVisible), static s => s.IsSourceTextVisible);

        var server = _serverRow = WinoSettingsRows.Action(vm.ServerRowTitle, vm.ServerRowDescription, WinoIconGlyph.GlobeDesktop);
        Settings.Bind(vm, nameof(vm.ServerRowTitle), static s => s.ServerRowTitle, value => server.SetTitle(value ?? string.Empty));
        Settings.Subtitle(server, vm, nameof(vm.ServerRowDescription), static s => s.ServerRowDescription);
        WinoSettingsRows.Suffix(server, Settings.LabelSuffix(vm, nameof(vm.ServerStatusText), static s => s.ServerStatusText));
        var action = WinoSettingsRows.Suffix(server, Settings.Button(vm.ServerActionText, vm.OpenServerSettingsCommand));
        Settings.Bind(vm, nameof(vm.ServerActionText), static s => s.ServerActionText, value =>
        {
            action.SetLabel(value ?? string.Empty);
            WinoA11y.Rename(action, value ?? string.Empty);
        });
        Settings.Visible(server, vm, nameof(vm.IsServerRowVisible), static s => s.IsServerRowVisible);

        var rows = Page.AddGroup(null, null, text, server);
        Settings.Bind(vm, nameof(vm.State), static s => s.IsSourceTextVisible || s.IsServerRowVisible, rows.Root.SetVisible);
        // Without cards the text row carries the "Syncs with" title itself.
        Settings.Bind(vm, nameof(vm.IsSourceChoiceVisible), static s => s.IsSourceChoiceVisible,
            cards => rows.Title = cards ? null : Translator.AccountApps_SyncsWith);
    }

    private void RebuildCards()
    {
        if (_cardBox is not { } box || IsReleased) return;
        foreach (var (_, card) in _cards)
        {
            box.Remove(card.Root);
            card.Dispose();
        }
        _cards.Clear();

        var vm = ViewModel;
        foreach (var option in vm.SourceOptions.ToList())
        {
            var card = new WinoSourceChoiceCard(option.Title, option.Description,
                option.Mode == AccountCapabilityMode.Local ? WinoIconGlyph.Desktop : WinoIconGlyph.Globe)
            {
                IsSelected = Equals(vm.SelectedSourceOption, option),
                IsEnabled = !vm.IsChangingSource
            };
            card.Clicked += () =>
            {
                if (!Equals(vm.SelectedSourceOption, option)) vm.SelectedSourceOption = option;
            };
            _cards.Add((option, card));
            box.Append(card.Root);
        }
    }

    // The ViewModel raises the property again with the same value after a refused or redirected change, so every
    // notification re-applies the selection.
    private void SelectCards(AccountAppSourceOption? selected)
    {
        foreach (var (option, card) in _cards) card.IsSelected = Equals(selected, option);
    }

    protected override Task DeactivateAsync()
    {
        foreach (var (_, card) in _cards) card.Dispose();
        _cards.Clear();
        return base.DeactivateAsync();
    }

#if DEBUG
    public AccountAppItemViewModel DebugAppItem => ViewModel.AppItem;

    public bool DebugChooseSource(AccountCapabilityMode mode)
    {
        var option = ViewModel.SourceOptions.FirstOrDefault(candidate => candidate.Mode == mode);
        if (option is null) return false;
        if (!Equals(ViewModel.SelectedSourceOption, option)) ViewModel.SelectedSourceOption = option;
        return true;
    }

    public void DebugPressServer()
    {
        if (ViewModel.OpenServerSettingsCommand.CanExecute(null)) ViewModel.OpenServerSettingsCommand.Execute(null);
    }

    internal virtual string DebugDescribeContent() => string.Empty;

    public string DebugDescribe()
    {
        var vm = ViewModel;
        var text = new StringBuilder();
        text.AppendLine($"page={GetType().Name} title={PageTitle} on={vm.AppItem.IsOn} canToggle={vm.AppItem.CanToggle} busy={vm.AppItem.IsBusy} lastApp={vm.AppItem.IsLastApp}"
                        + $" needsSignIn={vm.NeedsSignIn} isOn={vm.IsOn} isOff={vm.IsOff} changing={vm.IsChangingSource} mode={vm.State?.Mode}");
        text.AppendLine($"cards={(_cardBox?.GetVisible() == true ? "visible" : "hidden")} " + string.Join(" ; ", _cards.Select(static entry => entry.Card.Describe())));
        text.AppendLine($"source={(vm.IsSourceTextVisible ? vm.SourceText + "|" + vm.SourceDescription : "-")}");
        text.AppendLine($"server={(_serverRow?.GetVisible() == true ? $"{vm.ServerRowTitle}|{vm.ServerRowDescription}|{vm.ServerStatusText}|{vm.ServerActionText}" : "-")}");
        text.Append(DebugDescribeContent());
        return text.ToString();
    }
#endif
}

#if DEBUG
internal interface IAccountAppPageDebug
{
    static IAccountAppPageDebug? Current { get; set; }

    AccountAppItemViewModel DebugAppItem { get; }

    bool DebugChooseSource(AccountCapabilityMode mode);

    void DebugPressServer();

    string DebugDescribe();
}
#endif
