using System.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Bindings;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Onboarding;

/// <summary>
/// The capability cards (Windows AccountCapabilityPicker, macOS AccountCapabilityPicker): one card per capability (Mail,
/// Calendar, Contacts, To Do) with the app glyph, title, description, the current choice and an on/off switch. Expanding a
/// card shows where the capability lives as radio rows with their consequences; a provider mode the provider cannot serve
/// stays listed, insensitive, with the reason. All state lives in the shared <see cref="AccountCapabilitySelection"/>.
/// </summary>
internal sealed class AccountCapabilityPicker
{
    private readonly AccountCapabilitySelection _selection;
    private readonly Func<string> _mailProviderLabel;
    private readonly CapabilityCard _mail, _calendar, _contacts, _tasks;
    private readonly MainLoop.Coalescer _refresh;

    public AccountCapabilityPicker(AccountCapabilitySelection selection, Func<string> mailProviderLabel, BindingScope scope)
    {
        _selection = selection;
        _mailProviderLabel = mailProviderLabel;
        _refresh = scope.Own(new MainLoop.Coalescer(() => Refresh(initial: false)));

        _mail = new CapabilityCard(scope, Translator.ProviderSelection_UseForMail, selection.MailDescription, WinoIconGlyph.Mail,
            on => selection.IsMailEnabled = on, provider: null, local: null);
        _calendar = new CapabilityCard(scope, Translator.ProviderSelection_UseForCalendar, Translator.ProviderSelection_CalendarStepDescription, WinoIconGlyph.Calendar,
            on => selection.IsCalendarEnabled = on,
            provider: () => selection.IsCalendarProviderSelected = true,
            local: () => selection.IsCalendarLocalSelected = true, Translator.ProviderSelection_Why_CalendarLocal_Device);
        _contacts = new CapabilityCard(scope, Translator.ProviderSelection_UseForContacts, Translator.ProviderSelection_ContactsStepDescription, WinoIconGlyph.People,
            on => selection.IsContactEnabled = on,
            provider: () => selection.IsContactProviderSelected = true,
            local: () => selection.IsContactLocalSelected = true, Translator.ProviderSelection_Why_ContactsLocal_Device);
        _tasks = new CapabilityCard(scope, Translator.ProviderSelection_UseForTasks, Translator.ProviderSelection_TasksStepDescription, WinoIconGlyph.TaskList,
            on => selection.IsTaskEnabled = on,
            provider: () => selection.IsTaskProviderSelected = true,
            local: () => selection.IsTaskLocalSelected = true, Translator.ProviderSelection_Why_TasksLocal_Device);

        Root = Adw.PreferencesGroup.New();
        Root.Add(_mail.Row);
        Root.Add(_calendar.Row);
        Root.Add(_contacts.Row);
        Root.Add(_tasks.Row);

        PropertyChangedEventHandler changed = (_, _) => _refresh.Request();
        selection.PropertyChanged += changed;
        scope.Own(new ActionDisposable(() => selection.PropertyChanged -= changed));
        Refresh(initial: true);
    }

    public Adw.PreferencesGroup Root { get; }

    /// <summary>Re-reads labels that come from outside the selection (the mail provider name).</summary>
    public void QueueRefresh() => _refresh.Request();

    private void Refresh(bool initial)
    {
        var s = _selection;
        _mail.SetDescription(s.MailDescription);
        _mail.Apply(s.IsMailEnabled, s.IsMailEnabled ? string.Format(Translator.CapabilityPicker_SyncWith, _mailProviderLabel()) : Translator.ProviderSelection_ModeOff, initial);

        _calendar.SetProvider(s.CalendarProviderOptionText, s.CalendarProviderOptionDescription, s.IsCalendarProviderSelected, s.IsCalendarProviderModeAvailable);
        _calendar.SetLocal(s.IsCalendarLocalSelected);
        _calendar.Apply(s.IsCalendarEnabled, Summary(s.CalendarMode, s.CalendarProviderOptionText), initial);

        _contacts.SetProvider(s.ContactProviderOptionText, s.ContactProviderOptionDescription, s.IsContactProviderSelected, s.IsContactProviderModeAvailable);
        _contacts.SetLocal(s.IsContactLocalSelected);
        _contacts.Apply(s.IsContactEnabled, Summary(s.ContactMode, s.ContactProviderOptionText), initial);

        _tasks.SetProvider(s.TaskProviderOptionText, s.IsTaskProviderModeAvailable ? s.TaskProviderOptionDescription : s.TaskLocalOnlyNote,
            s.IsTaskProviderSelected, s.IsTaskProviderModeAvailable);
        _tasks.SetLocal(s.IsTaskLocalSelected);
        _tasks.Apply(s.IsTaskEnabled, Summary(s.TaskMode, s.TaskProviderOptionText), initial);
    }

    private static string Summary(AccountCapabilityMode mode, string providerText) => mode switch
    {
        AccountCapabilityMode.Provider => providerText,
        AccountCapabilityMode.Local => Translator.CapabilityPicker_LocalOption_Device,
        _ => Translator.ProviderSelection_ModeOff
    };

#if DEBUG
    public string Describe() => string.Join('\n', new[] { ("mail", _mail), ("calendar", _calendar), ("contacts", _contacts), ("tasks", _tasks) }
        .Select(static entry => $"capability {entry.Item1} {entry.Item2.Describe()}")) + "\n";

    /// <summary>Acts as a click on the card's switch, a radio or the expander.</summary>
    public bool DebugSet(string card, string action)
    {
        var target = card switch { "mail" => _mail, "calendar" => _calendar, "contacts" => _contacts, "tasks" => _tasks, _ => null };
        return target is not null && target.DebugSet(action);
    }
#endif

    /// <summary>One capability: the card (an expander, or a plain row for Mail), its summary and switch, and the mode radios.</summary>
    private sealed class CapabilityCard
    {
        private readonly Gtk.Label _summary = Gtk.Label.New(string.Empty);
        private readonly Gtk.Switch _switch = Gtk.Switch.New();
        private readonly OptionRow? _provider;
        private readonly OptionRow? _local;
        private readonly Adw.ExpanderRow? _expander;
        private readonly Adw.ActionRow? _plain;
        private bool _applying;
        private bool? _lastEnabled;

        public CapabilityCard(BindingScope scope, string title, string description, WinoIconGlyph icon, Action<bool> toggle,
            Action? provider, Action? local, string? localDescription = null)
        {
            _summary.AddCssClass("dim-label");
            // Wraps by word rather than ellipsizing; its minimum width (set per text) makes the description give way first.
            _summary.SetWrap(true);
            _summary.SetWrapMode(Pango.WrapMode.Word);
            _summary.SetMaxWidthChars(18);
            _summary.SetJustify(Gtk.Justification.Right);
            _summary.SetXalign(1);
            _switch.SetValign(Gtk.Align.Center);
            WinoA11y.Label(_switch, title);
            scope.Own(GtkSignals.OnNotify(_switch, "active", () =>
            {
                if (!_applying) toggle(_switch.GetActive());
            }));

            if (provider is not null && local is not null)
            {
                _provider = new OptionRow(scope, provider, null, null);
                _local = new OptionRow(scope, local, Translator.CapabilityPicker_LocalOption_Device, localDescription);
                _local.Check.SetGroup(_provider.Check);
                _expander = WinoSettingsRows.Expander(title, description, icon, false, _provider.Row, _local.Row);
                // An expander lays its suffixes out from the end.
                WinoSettingsRows.Suffix(_expander, _switch);
                WinoSettingsRows.Suffix(_expander, _summary);
                Row = _expander;
            }
            else
            {
                _plain = WinoSettingsRows.Action(title, description, icon);
                WinoSettingsRows.Suffix(_plain, _summary);
                WinoSettingsRows.Suffix(_plain, _switch);
                // The Mail card has no expander arrow; a transparent one keeps its switch in line with the others.
                var spacer = Gtk.Image.NewFromIconName("pan-down-symbolic");
                spacer.SetOpacity(0);
                spacer.AddCssClass("expander-row-arrow");
                WinoSettingsRows.Suffix(_plain, spacer);
                _plain.SetActivatableWidget(_switch);
                Row = _plain;
            }
        }

        public Adw.PreferencesRow Row { get; }

        public void SetDescription(string description)
        {
            if (_plain is not null && _plain.GetSubtitle() != description) _plain.SetSubtitle(description);
        }

        public void SetProvider(string title, string description, bool selected, bool available) => _provider?.Set(title, description, selected, available);

        public void SetLocal(bool selected) => _local?.Set(null, null, selected, true);

        /// <summary>
        /// Follows the Windows IsExpanded binding: turning a capability on opens its card and turning it off closes it. In
        /// between the user can open or close the card freely.
        /// </summary>
        public void Apply(bool enabled, string summary, bool initial)
        {
            _applying = true;
            try { if (_switch.GetActive() != enabled) _switch.SetActive(enabled); }
            finally { _applying = false; }
            if (_summary.GetText() != summary)
            {
                _summary.SetText(summary);
                _summary.SetWidthChars(Math.Min(summary.Length, 18));
            }
            _summary.SetOpacity(enabled ? 1 : 0.6);
            _provider?.SetGroupEnabled(enabled);
            _local?.SetGroupEnabled(enabled);
            if (_expander is not null && _lastEnabled != enabled && (initial || _lastEnabled is not null)) _expander.SetExpanded(enabled);
            _lastEnabled = enabled;
        }

#if DEBUG
        public string Describe()
            => $"on={_switch.GetActive()} summary='{_summary.GetText()}' expanded={_expander?.GetExpanded().ToString() ?? "none"}"
               + (_provider is null || _local is null ? string.Empty : $" provider={_provider.Describe()} local={_local.Describe()}");

        public bool DebugSet(string action)
        {
            switch (action)
            {
                case "on" or "off":
                    _switch.SetActive(action == "on");
                    return true;
                case "provider":
                    return _provider?.DebugSelect() ?? false;
                case "local":
                    return _local?.DebugSelect() ?? false;
                case "expand" or "collapse":
                    _expander?.SetExpanded(action == "expand");
                    return _expander is not null;
                default:
                    return false;
            }
        }
#endif
    }

    /// <summary>A radio choice with its consequence as the subtitle.</summary>
    private sealed class OptionRow
    {
        private bool _available = true;
        private bool _groupEnabled = true;
        private bool _applying;

        public OptionRow(BindingScope scope, Action select, string? title, string? description)
        {
            Check = Gtk.CheckButton.New();
            Check.SetValign(Gtk.Align.Center);
            Row = Adw.ActionRow.New();
            Row.SetUseMarkup(false);
            Row.SetTitle(title ?? string.Empty);
            if (description is not null) Row.SetSubtitle(description);
            Row.SetSubtitleLines(0);
            if (title is not null) WinoA11y.Label(Check, title);
            Row.AddPrefix(Check);
            Row.SetActivatableWidget(Check);
            var check = Check;
            scope.Own(GtkSignals.OnToggled(Check, () =>
            {
                if (!_applying && check.GetActive()) select();
            }));
        }

        public Gtk.CheckButton Check { get; }
        public Adw.ActionRow Row { get; }

        public void Set(string? title, string? description, bool selected, bool available)
        {
            if (title is not null && Row.GetTitle() != title)
            {
                Row.SetTitle(title);
                WinoA11y.Label(Check, title);
            }
            if (description is not null && Row.GetSubtitle() != description) Row.SetSubtitle(description);
            _applying = true;
            try { if (Check.GetActive() != selected) Check.SetActive(selected); }
            finally { _applying = false; }
            _available = available;
            ApplyEnabled();
        }

        public void SetGroupEnabled(bool enabled)
        {
            _groupEnabled = enabled;
            ApplyEnabled();
        }

        private void ApplyEnabled()
        {
            var enabled = _available && _groupEnabled;
            if (Row.GetSensitive() != enabled) Row.SetSensitive(enabled);
        }

#if DEBUG
        public string Describe() => $"'{Row.GetTitle()}'/{(Check.GetActive() ? "selected" : "-")}/{(Row.GetSensitive() ? "enabled" : "disabled")}";

        public bool DebugSelect()
        {
            if (!Row.GetSensitive()) return false;
            Check.SetActive(true);
            return true;
        }
#endif
    }
}
