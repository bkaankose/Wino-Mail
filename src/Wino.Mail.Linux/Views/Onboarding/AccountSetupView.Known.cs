using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Presentation;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Views.Onboarding;

/// <summary>
/// The account so far (Windows KnownRow): a pill with the provider mark on the account colour, the address and what is
/// known under it, the discovery spinner, and Change beside it. Change offers "Change address" and "Change provider";
/// the provider picker is a dialog, so the not-found and failure bars open it from their own buttons too.
/// </summary>
internal sealed partial class AccountSetupView
{
    private Gtk.MenuButton _change = null!;
    private string? _knownKey;
    private Adw.Dialog? _providerPicker;

    private Gtk.Widget BuildKnownRow()
    {
        Bindings.Own(new ActionDisposable(() => _providerPicker?.ForceClose()));
        var icon = new WinoIcon(WinoIconGlyph.Mail, 16, WinoIconMode.FollowStyle);
        var circle = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        circle.AddCssClass("wino-setup-known-mark");
        circle.SetSizeRequest(32, 32);
        circle.SetValign(Gtk.Align.Center);
        icon.Root.SetHexpand(true);
        circle.Append(icon.Root);

        var identity = Gtk.Label.New(string.Empty);
        identity.AddCssClass("heading");
        identity.SetXalign(0);
        identity.SetEllipsize(Pango.EllipsizeMode.End);
        identity.SetMaxWidthChars(32);
        var chips = OnboardingUi.Caption();
        chips.SetMaxWidthChars(40);
        var text = OnboardingUi.Column(1, identity, chips);
        text.SetValign(Gtk.Align.Center);
        var spinner = OnboardingUi.Spinner();
        _form.When(spinner, s => s.IsResolvingVisible);

        var pill = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
        pill.AddCssClass("card");
        pill.AddCssClass("wino-setup-known");
        pill.Append(circle);
        pill.Append(text);
        pill.Append(spinner);

        var changeAddress = Gtk.Button.NewWithLabel(Translator.AccountSetup_ChangeAddress);
        changeAddress.AddCssClass("flat");
        var changeProvider = Gtk.Button.NewWithLabel(Translator.AccountSetup_ChangeProvider);
        changeProvider.AddCssClass("flat");
        _form.When(changeProvider, s => s.IsChangeProviderVisible);
        var menu = OnboardingUi.Column(2, changeAddress, changeProvider);
        var popover = Gtk.Popover.New();
        popover.SetChild(menu);
        _change = Gtk.MenuButton.New();
        _change.SetLabel(Translator.AccountSetup_Change);
        _change.AddCssClass("flat");
        _change.SetValign(Gtk.Align.Center);
        _change.SetPopover(popover);
        _form.When(_change, s => s.IsChangeAddressVisible);
        Bindings.Own(GtkSignals.OnClicked(changeAddress, () =>
        {
            popover.Popdown();
            _form.Run(ViewModel.ChangeAddressCommand);
        }));
        Bindings.Own(GtkSignals.OnClicked(changeProvider, () =>
        {
            popover.Popdown();
            MainLoop.Post(ShowProviderPicker);
        }));

        var line = Gtk.Box.New(Gtk.Orientation.Horizontal, 4);
        line.SetHalign(Gtk.Align.Center);
        line.Append(pill);
        line.Append(_change);
        _form.When(line, s => s.IsKnownCardVisible);

        _form.Apply(() =>
        {
            WinoA11y.Label(pill, ViewModel.KnownText);
            WinoA11y.Description(pill, ViewModel.RouteEvidenceText);
            WinoA11y.Label(spinner, ViewModel.DiscoveryText);
            WinoA11y.Description(_change, ViewModel.ChangeAddressAutomationName);
            WinoA11y.Rename(changeProvider, ViewModel.ChangeProviderAutomationName is { Length: > 0 } name ? name : Translator.AccountSetup_ChangeProvider);
            RenderKnown(identity, chips, pill, circle, icon);
        });
        return line;
    }

    /// <summary>
    /// The address (or a local account's name) is the first line; the provider and capabilities follow under it, and why
    /// Wino chose the provider is the pill's tooltip. The mark is the generic mail glyph until discovery names the provider.
    /// </summary>
    private void RenderKnown(Gtk.Label identity, Gtk.Label chips, Gtk.Widget pill, Gtk.Widget circle, WinoIcon icon)
    {
        var vm = ViewModel;
        var identityText = string.Empty;
        var reason = string.Empty;
        var hasProvider = false;
        var items = new List<AccountSetupKnownItem>();
        foreach (var item in vm.KnownItems)
        {
            switch (item.Kind)
            {
                case AccountSetupKnownItemKind.Identity: identityText = item.Text; break;
                case AccountSetupKnownItemKind.Reason: reason = item.Text; break;
                default:
                    hasProvider |= item.Kind == AccountSetupKnownItemKind.Provider;
                    if (!string.IsNullOrWhiteSpace(item.Text)) items.Add(item);
                    break;
            }
        }

        OnboardingUi.SetText(identity, identityText);
        pill.SetTooltipText(reason.Length > 0 ? reason : null);
        var key = string.Join('\u001f', items.Select(item => $"{item.Kind}:{item.Text}"));
        if (key != _knownKey)
        {
            _knownKey = key;
            chips.SetText(string.Join("  ·  ", items.Select(item => item.Kind == AccountSetupKnownItemKind.SignIn ? "✓ " + item.Text : item.Text)));
            var help = string.Join('\n', items.Select(item => item.HelpText).Where(text => !string.IsNullOrEmpty(text)));
            chips.SetTooltipText(help.Length > 0 ? help : null);
        }
        OnboardingUi.SetVisible(chips, items.Count > 0);

        var providerGlyph = hasProvider ? MailProviderGlyphs.GetGlyph(vm.RouteProviderType, vm.RouteSpecialImapProvider) : WinoIconGlyph.Mail;
        icon.SetIcon(vm.IsLocalOnlyMode ? WinoIconGlyph.Laptop : providerGlyph == WinoIconGlyph.None ? WinoIconGlyph.Mail : providerGlyph);
        foreach (var name in circle.GetCssClasses() ?? [])
            if (name.StartsWith("wino-fill-", StringComparison.Ordinal)) circle.RemoveCssClass(name);
        if (vm.SelectedColor?.Hex is { Length: > 0 } hex && WinoTint.FillClassFor(hex, null) is { } fill) circle.AddCssClass(fill);
    }

    /// <summary>The providers Wino can set up, the current one marked; choosing one runs discovery for it.</summary>
    private void ShowProviderPicker()
    {
        if (IsReleased) return;
        _providerPicker?.ForceClose();
        var vm = ViewModel;
        var dialog = Adw.Dialog.New();
        dialog.SetTitle(Translator.AccountSetup_ProviderPicker_Title);
        dialog.SetContentWidth(380);
        dialog.SetContentHeight(520);

        var search = Gtk.SearchEntry.New();
        WinoA11y.Label(search, Translator.AccountSetup_ProviderPicker_Title);
        var rows = OnboardingUi.Column(2);
        var current = vm.CurrentProviderChoice;
        var buttons = new List<(Gtk.Button Button, AccountSetupProviderChoice Choice)>();
        var scope = new Wino.Presentation.Bindings.BindingScope();
        foreach (var choice in vm.ProviderChoices)
        {
            var button = AccountSetupProviderChoiceRow.Create(choice, ReferenceEquals(choice, current));
            scope.Own(GtkSignals.OnClicked(button, () =>
            {
                dialog.Close();
                _form.Run(ViewModel.ChangeProviderCommand, choice);
            }));
            buttons.Add((button, choice));
            rows.Append(button);
        }
        scope.Own(GtkSignals.OnChanged(search, () =>
        {
            var query = search.GetText().Trim();
            foreach (var (button, choice) in buttons)
                button.SetVisible(query.Length == 0 || choice.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        }));

        var caption = OnboardingUi.Caption(vm.ProviderPickerCaption);
        caption.SetVisible(!string.IsNullOrEmpty(vm.ProviderPickerCaption));
        var column = OnboardingUi.Column(8, caption, search, rows);
        column.SetMarginTop(6);
        column.SetMarginBottom(12);
        column.SetMarginStart(12);
        column.SetMarginEnd(12);
        var scroller = Gtk.ScrolledWindow.New();
        scroller.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        scroller.SetVexpand(true);
        scroller.SetChild(column);
        var toolbar = Adw.ToolbarView.New();
        toolbar.AddTopBar(Adw.HeaderBar.New());
        toolbar.SetContent(scroller);
        dialog.SetChild(toolbar);
        scope.Own(GtkSignals.Connect<Adw.Dialog>(h => dialog.OnClosed += h, h => dialog.OnClosed -= h, (_, _) =>
        {
            if (ReferenceEquals(_providerPicker, dialog)) _providerPicker = null;
            MainLoop.Post(scope.Dispose);
        }));
        _providerPicker = dialog;
        dialog.Present(_root);
        search.GrabFocus();
    }
}
