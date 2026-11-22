using Wino.Core.Domain.Enums;
using MimeKit;
using Wino.Core.Domain;
using Wino.Mail.ViewModels;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Mail.Compose;

internal sealed partial class ComposeView
{
    private static readonly MessageImportance[] Importances = [MessageImportance.High, MessageImportance.Normal, MessageImportance.Low];
    private readonly Gtk.DropDown _fromPicker = Gtk.DropDown.New(null, null);
    private readonly Gtk.Label _fromLabel = Gtk.Label.New(string.Empty);
    private readonly Gtk.Entry _subject = Gtk.Entry.New();
    private readonly Gtk.MenuButton _importance = Gtk.MenuButton.New();
    private readonly Gtk.ToggleButton[] _importanceChoices =
    [
        Gtk.ToggleButton.NewWithLabel(Translator.Composer_HighImportance),
        Gtk.ToggleButton.NewWithLabel(Translator.Composer_NormalImportance),
        Gtk.ToggleButton.NewWithLabel(Translator.Composer_LowImportance)
    ];
    private readonly Gtk.ToggleButton _readReceipt = WinoIcons.ToggleButton(WinoIconGlyph.MailCheckmark, Translator.Composer_RequestReadReceipt);
    private readonly Gtk.Box _ccBccButtons = Gtk.Box.New(Gtk.Orientation.Horizontal, 2);
    private readonly Gtk.Button _ccButton = Gtk.Button.NewWithLabel("Cc");
    private readonly Gtk.Button _bccButton = Gtk.Button.NewWithLabel("Bcc");
    private Gtk.Widget? _ccRow;
    private Gtk.Widget? _bccRow;
    private bool _updatingFrom;

    private static string Caption(string key) => key.Trim().TrimEnd(':');

    private Gtk.Widget BuildFields()
    {
        _fromLabel.SetXalign(0);
        _fromLabel.SetHexpand(true);
        _fromLabel.SetSelectable(true);
        _fromLabel.SetEllipsize(Pango.EllipsizeMode.End);
        WinoA11y.Label(_fromLabel, Caption(Translator.ComposerFrom));
        _fromPicker.SetHexpand(true);
        _fromPicker.AddCssClass("flat");
        WinoA11y.Label(_fromPicker, Caption(Translator.ComposerFrom));

        foreach (var button in new[] { _ccButton, _bccButton })
        {
            button.AddCssClass("flat");
            button.AddCssClass("caption");
            WinoA11y.Rename(button, Translator.Composer_CcBcc);
        }
        _ccBccButtons.Append(_ccButton);
        _ccBccButtons.Append(_bccButton);

        _subject.SetHasFrame(false);
        _subject.SetHexpand(true);
        _subject.SetPlaceholderText(Caption(Translator.ComposerSubject));
        WinoA11y.Label(_subject, Caption(Translator.ComposerSubject));
        _importance.AddCssClass("flat");
        _importance.SetChild(WinoIcons.Label(WinoIconGlyph.Important));
        _importance.SetTooltipText(Translator.Composer_ImportanceDescription);
        WinoA11y.Label(_importance, Translator.Composer_Importance);
        var choices = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
        foreach (var choice in _importanceChoices)
        {
            choice.AddCssClass("flat");
            if (!ReferenceEquals(choice, _importanceChoices[0])) choice.SetGroup(_importanceChoices[0]);
            if (choice.GetChild() is Gtk.Label text) text.SetXalign(0);
            choices.Append(choice);
        }
        var popover = Gtk.Popover.New();
        popover.SetChild(choices);
        _importance.SetPopover(popover);
        _readReceipt.SetTooltipText(Translator.Composer_RequestReadReceipt);

        var fields = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        fields.AddCssClass("wino-compose-fields");
        fields.Append(FieldRow(Caption(Translator.ComposerFrom), _fromPicker, _fromLabel));
        fields.Append(FieldRow(Caption(Translator.ComposerTo), ToField, _ccBccButtons));
        fields.Append(_ccRow = FieldRow("Cc", CcField));
        fields.Append(_bccRow = FieldRow("Bcc", BccField));
        fields.Append(FieldRow(Caption(Translator.ComposerSubject), _subject, _importance, _readReceipt));
        return fields;
    }

    private static Gtk.Widget FieldRow(string label, params Gtk.Widget[] content)
    {
        var caption = Gtk.Label.New(label);
        caption.AddCssClass("dim-label");
        caption.SetXalign(1);
        caption.SetSizeRequest(52, -1);
        caption.SetValign(Gtk.Align.Center);
        var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
        row.SetMarginTop(4);
        row.SetMarginBottom(4);
        row.SetMarginStart(16);
        row.SetMarginEnd(16);
        row.Append(caption);
        foreach (var widget in content) row.Append(widget);
        content[0].SetHexpand(true);
        var host = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        host.Append(row);
        host.Append(Gtk.Separator.New(Gtk.Orientation.Horizontal));
        return host;
    }

    private void BindFields()
    {
        Bind.BindText(_subject, nameof(ComposePageViewModel.Subject), static vm => vm.Subject, static (vm, text) => vm.Subject = text);
        Bindings.Own(GtkSignals.OnChanged(_subject, () =>
        {
            if (_subject.GetFocusChild() is not null) ScheduleAutosave();
        }));
        Bind.BindAny(static vm => vm.IsImportanceSelected ? vm.SelectedMessageImportance : MessageImportance.Normal, ShowImportance);
        for (var index = 0; index < Importances.Length; index++)
        {
            var importance = Importances[index];
            var choice = _importanceChoices[index];
            Bindings.Own(GtkSignals.OnToggled(choice, () =>
            {
                if (!choice.GetActive()) return;
                _importance.Popdown();
                var current = ViewModel.IsImportanceSelected ? ViewModel.SelectedMessageImportance : MessageImportance.Normal;
                if (current == importance) return;
                ViewModel.SelectedMessageImportance = importance;
                ViewModel.IsImportanceSelected = importance != MessageImportance.Normal;
                ScheduleAutosave();
            }));
        }
        Bind.BindActive(_readReceipt, nameof(ComposePageViewModel.IsReadReceiptRequested), static vm => vm.IsReadReceiptRequested, static (vm, on) => vm.IsReadReceiptRequested = on);
        Bind.Bind(nameof(ComposePageViewModel.IsCCBCCVisible), static vm => vm.IsCCBCCVisible, visible =>
        {
            _ccRow?.SetVisible(visible);
            _bccRow?.SetVisible(visible);
            _ccBccButtons.SetVisible(!visible);
        });
        Bindings.Own(GtkSignals.OnClicked(_ccButton, () => RevealCopies(CcField)));
        Bindings.Own(GtkSignals.OnClicked(_bccButton, () => RevealCopies(BccField)));
        Bind.Bind(nameof(ComposePageViewModel.AvailableAliases), static vm => vm.AvailableAliases, _ => UpdateFrom());
        Bind.Bind(nameof(ComposePageViewModel.SelectedAlias), static vm => vm.SelectedAlias, _ => UpdateFrom());
        Bind.Bind(nameof(ComposePageViewModel.ComposingAccount), static vm => vm.ComposingAccount, _ => UpdateFrom());
        Bindings.Own(GtkSignals.OnNotify(_fromPicker, "selected", () =>
        {
            if (_updatingFrom) return;
            var index = (int)_fromPicker.GetSelected();
            if (ViewModel.AvailableAliases is { } aliases && index >= 0 && index < aliases.Count) ViewModel.SelectedAlias = aliases[index];
        }));
    }

    private void ShowImportance(MessageImportance importance)
    {
        var index = Array.IndexOf(Importances, importance);
        if (index >= 0 && !_importanceChoices[index].GetActive()) _importanceChoices[index].SetActive(true);
        var label = index >= 0 ? _importanceChoices[index].GetLabel() ?? Translator.Composer_Importance : Translator.Composer_Importance;
        _importance.SetTooltipText(label);
        WinoA11y.Label(_importance, $"{Translator.Composer_Importance}: {label}");
        if (importance == MessageImportance.Normal) _importance.RemoveCssClass("accent");
        else _importance.AddCssClass("accent");
    }

    private void RevealCopies(Gtk.Widget field)
    {
        ViewModel.IsCCBCCVisible = true;
        field.GrabFocus();
    }

    private void UpdateFrom()
    {
        var account = ViewModel.ComposingAccount;
        var aliases = ViewModel.AvailableAliases;
        var items = new List<string>();
        var selected = 0;
        if (aliases is { Count: > 0 })
        {
            foreach (var alias in aliases)
            {
                var name = string.IsNullOrWhiteSpace(alias.AliasSenderName) ? account?.SenderName : alias.AliasSenderName;
                items.Add(string.IsNullOrWhiteSpace(name) ? alias.AliasAddress : $"{name} <{alias.AliasAddress}>");
            }
            selected = ViewModel.SelectedAlias is { } current ? Math.Max(0, aliases.IndexOf(current)) : 0;
        }
        else if (account is not null)
        {
            items.Add(string.IsNullOrWhiteSpace(account.SenderName) ? account.Address : $"{account.SenderName} <{account.Address}>");
        }
        var choice = items.Count > 1;
        _updatingFrom = true;
        try
        {
            _fromPicker.SetModel(Gtk.StringList.New(items.ToArray()));
            if (items.Count > 0) _fromPicker.SetSelected((uint)selected);
        }
        finally { _updatingFrom = false; }
        _fromPicker.SetVisible(choice);
        _fromLabel.SetText(items.Count > 0 ? items[selected] : string.Empty);
        _fromLabel.SetVisible(!choice);
    }
}
