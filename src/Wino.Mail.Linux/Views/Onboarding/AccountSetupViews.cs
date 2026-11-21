using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Bindings;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Onboarding;

/// <summary>
/// One mark of the address step's "Works with" shelf (Windows AccountSetupProviderShelfItem): a round brand glyph, or
/// a capsule with a label for IMAP and POP3. The matched provider gets an accent ring and the others dim. It is a
/// picture, not a button.
/// </summary>
internal sealed class AccountSetupShelfItem
{
    private readonly Gtk.Box _root;
    private readonly WinoIcon _glyph = new(WinoIconGlyph.None, 18, WinoIconMode.Colorful);
    private readonly Gtk.Label _label = Gtk.Label.New(string.Empty);
    private AccountSetupProviderShelfItem? _item;

    public AccountSetupShelfItem()
    {
        _root = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        _root.AddCssClass("wino-setup-shelf-item");
        _root.AccessibleRole = Gtk.AccessibleRole.Img;
        _root.SetValign(Gtk.Align.Center);
        _glyph.Root.SetHexpand(true);
        _label.AddCssClass("caption-heading");
        _label.SetHexpand(true);
        _root.Append(_glyph.Root);
        _root.Append(_label);
    }

    public Gtk.Widget Root => _root;

    public void Apply(AccountSetupProviderShelfItem item)
    {
        if (_item == item) return;
        _item = item;
        _glyph.SetIcon(item.HasGlyph ? item.Glyph : WinoIconGlyph.None);
        _glyph.Root.SetVisible(item.HasGlyph);
        _label.SetText(item.Label);
        _label.SetVisible(item.HasLabel);
        if (item.HasLabel) _root.AddCssClass("wino-setup-shelf-label");
        else _root.RemoveCssClass("wino-setup-shelf-label");
        if (item.IsMatched) _root.AddCssClass("wino-setup-matched");
        else _root.RemoveCssClass("wino-setup-matched");
        _root.SetOpacity(item.IsDimmed ? 0.4 : 1);
        _root.SetTooltipText(item.Title);
        WinoA11y.Label(_root, item.Title);
    }
}

/// <summary>One entry of the provider picker (Windows AccountSetupProviderChoice): a flat button spanning the row, so it can be clicked by pointer, keyboard and AT-SPI.</summary>
internal static class AccountSetupProviderChoiceRow
{
    public static Gtk.Button Create(AccountSetupProviderChoice choice, bool isCurrent)
    {
        var icon = new WinoIcon(choice.Glyph, 16, WinoIconMode.Colorful);
        var title = Gtk.Label.New(choice.Title);
        title.SetXalign(0);
        title.SetHexpand(true);
        title.SetEllipsize(Pango.EllipsizeMode.End);
        var box = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
        box.Append(icon.Root);
        box.Append(title);
        if (choice.HasDescription)
        {
            var description = Gtk.Label.New(choice.Description);
            description.AddCssClass("caption");
            description.AddCssClass("dim-label");
            box.Append(description);
        }
        if (isCurrent) box.Append(WinoIcons.Label(WinoIconGlyph.Checkmark, 14));

        var button = Gtk.Button.New();
        button.SetChild(box);
        button.AddCssClass("flat");
        button.AddCssClass("wino-setup-provider-choice");
        WinoA11y.Rename(button, choice.AutomationName);
        return button;
    }
}

/// <summary>
/// One sign-in method (Windows AccountSetupChoiceTemplate): a toggle button in one group, so pointer, keyboard and
/// assistive technology all select it the same way; the accent outline marks the selected card.
/// </summary>
internal sealed class AccountSetupChoiceCard
{
    public AccountSetupChoiceCard(AccountSetupChoice choice, Gtk.ToggleButton? group)
    {
        Choice = choice;
        var label = Gtk.Label.New(choice.Label);
        label.SetXalign(0);
        label.SetWrap(true);
        var box = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        box.Append(label);
        if (choice.HasCaption)
        {
            var caption = Gtk.Label.New(choice.Caption);
            caption.AddCssClass("dim-label");
            box.Append(caption);
        }
        Toggle = Gtk.ToggleButton.New();
        Toggle.SetChild(box);
        Toggle.AddCssClass("card");
        Toggle.AddCssClass("wino-setup-choice");
        if (group is not null) Toggle.SetGroup(group);
        WinoA11y.Rename(Toggle, choice.AutomationName);
    }

    public AccountSetupChoice Choice { get; }
    public Gtk.ToggleButton Toggle { get; }
}

/// <summary>
/// One app on the review step (Windows AccountSetupAppRowTemplate): a row with the app's glyph, title and status, the
/// source drop-down and the switch, then the server address, the note and the error under it. The rows live in the
/// review's group; the owner disposes the row's scope before it rebuilds.
/// </summary>
internal sealed class AccountSetupAppRowView
{
    private readonly Adw.ActionRow _row;
    private readonly Adw.ComboRow _source;
    private readonly Gtk.Switch _switch;
    private readonly Adw.EntryRow _server;
    private readonly Gtk.Label _note;
    private readonly Gtk.Label _error;
    private readonly string _sourceName;
    private bool _applying;

    public AccountSetupAppRowView(AccountSetupAppRow row, BindingScope scope)
    {
        Row = row;
        _row = WinoSettingsRows.Action(row.Title, null, row.Glyph);
        // The source is a row of its own: provider names are long, and a drop-down suffix squeezed the title to a letter per line.
        _source = WinoSettingsRows.Combo(string.Empty);
        _source.SetModel(Gtk.StringList.New([.. row.Options.Select(option => option.Label)]));
        _source.SetUseSubtitle(true);
        _sourceName = row.Kind == AccountSetupAppKind.Mail
            ? Translator.AccountSetup_Review_MailRange
            : string.Format(Translator.AccountSetup_Review_SourceOf, row.Title);
        _switch = Gtk.Switch.New();
        _switch.SetValign(Gtk.Align.Center);
        _switch.SetVisible(row.IsSwitchVisible);
        WinoA11y.Label(_switch, string.Format(Translator.AccountSetup_Review_UseApp, row.Title));
        _source.SetTitle(_sourceName);
        _row.AddSuffix(_switch);

        _server = WinoSettingsRows.Entry(Translator.AccountSetup_ServerAddressHeader);
        _server.SetInputPurpose(Gtk.InputPurpose.Url);
        WinoA11y.Description(_server, Translator.AccountSetup_ServerAddressDescription);
        _note = OnboardingUi.Caption();
        _error = OnboardingUi.Error();
        var notes = OnboardingUi.Column(2, _note, _error);
        Notes = WinoSettingsRows.Custom(notes);

        scope.Own(GtkSignals.OnNotify(_source, "selected", () =>
        {
            var index = (int)_source.GetSelected();
            if (!_applying && index >= 0 && index < Row.Options.Count) Row.SelectedOption = Row.Options[index];
        }));
        scope.Own(GtkSignals.OnNotify(_switch, "active", () =>
        {
            if (!_applying) Row.IsOn = _switch.GetActive();
        }));
        scope.Own(GtkSignals.OnChanged(_server, () =>
        {
            if (!_applying) Row.ServerUrl = _server.GetText();
        }));
        Apply();
    }

    public AccountSetupAppRow Row { get; }
    public Gtk.Widget Main => _row;
    public Gtk.Widget Source => _source;
    public Gtk.Widget Server => _server;
    public Adw.PreferencesRow Notes { get; }
    public Adw.EntryRow ServerField => _server;

    public void SetEnabled(bool enabled)
    {
        OnboardingUi.SetSensitive(_source, enabled);
        OnboardingUi.SetSensitive(_switch, enabled);
        OnboardingUi.SetSensitive(_server, enabled);
    }

    public void Apply()
    {
        _applying = true;
        try
        {
            var row = Row;
            var status = row.HasStatus ? row.StatusText : string.Empty;
            if (_row.GetSubtitle() != status) _row.SetSubtitle(status);
            if (_switch.GetActive() != row.IsOn) _switch.SetActive(row.IsOn);
            OnboardingUi.SetVisible(_source, row.IsSourceVisible);
            var index = row.SelectedOption is null ? -1 : row.Options.IndexOf(row.SelectedOption);
            if (index >= 0 && _source.GetSelected() != index) _source.SetSelected((uint)index);
            WinoA11y.Description(_source, row.AutomationName);
            OnboardingUi.SetVisible(_server, row.IsServerEntryShown);
            if (_server.GetText() != row.ServerUrl) _server.SetText(row.ServerUrl);
            OnboardingUi.SetText(_note, row.Note);
            OnboardingUi.SetVisible(_note, row.IsNoteShown);
            OnboardingUi.SetText(_error, row.Error);
            OnboardingUi.SetVisible(_error, row.HasError);
            OnboardingUi.SetVisible(Notes, row.IsNoteShown || row.HasError);
        }
        finally { _applying = false; }
    }
}
