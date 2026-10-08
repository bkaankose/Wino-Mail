using System.ComponentModel;
using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Onboarding;

/// <summary>
/// macOS counterpart of the Windows AccountCapabilityPicker: one expander card per capability
/// (Mail, Calendar, Contacts, To Do) with the app glyph, title and description, the current choice
/// and an on/off switch on the right. Expanding a card shows where the capability lives as radio
/// rows with their consequences; a provider mode the provider cannot serve stays listed, disabled,
/// with the reason. All state lives in the shared <see cref="AccountCapabilitySelection"/>.
/// </summary>
internal sealed class AccountCapabilityPicker : NSView
{
    private readonly AccountCapabilitySelection _selection;
    private readonly Func<string> _mailProviderLabel;
    private readonly IDispatcher _dispatcher;
    private readonly Action<Exception> _error;
    private readonly CapabilityCard _mail, _calendar, _contacts, _tasks;
    private bool _refreshQueued;

    /// <param name="mailOptions">Rows under the Mail card (the wizard's download range); null shows a plain card.</param>
    public AccountCapabilityPicker(AccountCapabilitySelection selection, Func<string> mailProviderLabel, NSView? mailOptions,
        BindingScope scope, IDispatcher dispatcher, Action<Exception> error)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        _selection = selection;
        _mailProviderLabel = mailProviderLabel;
        _dispatcher = dispatcher;
        _error = error;

        _mail = new CapabilityCard(Translator.ProviderSelection_UseForMail, selection.MailDescription, WinoIconGlyph.Mail,
            on => selection.IsMailEnabled = on, provider: null, local: null);
        if (mailOptions is not null) _mail.Expander.Add(mailOptions);
        else
        {
            _mail.Expander.HeaderCard.IsExpanderHeader = false;
            _mail.Expander.HeaderCard.ShowsChevron = false;
            _mail.Expander.HeaderCard.IsClickable = false;
        }

        _calendar = new CapabilityCard(Translator.ProviderSelection_UseForCalendar, Translator.ProviderSelection_CalendarStepDescription, WinoIconGlyph.Calendar,
            on => selection.IsCalendarEnabled = on,
            provider: new OptionRow(() => selection.IsCalendarProviderSelected = true),
            local: new OptionRow(() => selection.IsCalendarLocalSelected = true, Translator.CapabilityPicker_LocalOption, Translator.ProviderSelection_Why_CalendarLocal));
        _contacts = new CapabilityCard(Translator.ProviderSelection_UseForContacts, Translator.ProviderSelection_ContactsStepDescription, WinoIconGlyph.People,
            on => selection.IsContactEnabled = on,
            provider: new OptionRow(() => selection.IsContactProviderSelected = true),
            local: new OptionRow(() => selection.IsContactLocalSelected = true, Translator.CapabilityPicker_LocalOption, Translator.ProviderSelection_Why_ContactsLocal));
        _tasks = new CapabilityCard(Translator.ProviderSelection_UseForTasks, Translator.ProviderSelection_TasksStepDescription, WinoIconGlyph.TaskList,
            on => selection.IsTaskEnabled = on,
            provider: new OptionRow(() => selection.IsTaskProviderSelected = true),
            local: new OptionRow(() => selection.IsTaskLocalSelected = true, Translator.CapabilityPicker_LocalOption, Translator.ProviderSelection_Why_TasksLocal));

        var stack = WinoLayout.VStack(WinoSettingsStyle.CardSpacing, _mail.Expander, _calendar.Expander, _contacts.Expander, _tasks.Expander);
        foreach (var view in stack.ArrangedSubviews) view.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
        WinoLayout.Fill(stack, this);

        PropertyChangedEventHandler changed = (_, _) => QueueRefresh();
        selection.PropertyChanged += changed;
        scope.Own(new ActionDisposable(() => selection.PropertyChanged -= changed));
        Refresh(initial: true);
    }

    /// <summary>Re-reads labels that come from outside the selection (the mail provider name).</summary>
    public void QueueRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        _ = RefreshOnUIThread();
    }

    private async Task RefreshOnUIThread()
    {
        try { await _dispatcher.ExecuteOnUIThread(() => { _refreshQueued = false; Refresh(initial: false); }); }
        catch (Exception exception) { _error(exception); }
    }

    private void Refresh(bool initial)
    {
        var s = _selection;
        var mailLabel = string.Format(Translator.CapabilityPicker_SyncWith, _mailProviderLabel());
        _mail.Expander.HeaderCard.Description = s.MailDescription;
        _mail.Apply(s.IsMailEnabled, s.IsMailEnabled ? mailLabel : Translator.ProviderSelection_ModeOff, initial);

        _calendar.Provider!.Set(s.CalendarProviderOptionText,
            s.CalendarProviderOptionDescription,
            s.IsCalendarProviderSelected, s.IsCalendarProviderModeAvailable);
        _calendar.Local!.Set(null, null, s.IsCalendarLocalSelected, true);
        _calendar.Apply(s.IsCalendarEnabled, Summary(s.CalendarMode, s.CalendarProviderOptionText), initial);

        _contacts.Provider!.Set(s.ContactProviderOptionText,
            s.ContactProviderOptionDescription,
            s.IsContactProviderSelected, s.IsContactProviderModeAvailable);
        _contacts.Local!.Set(null, null, s.IsContactLocalSelected, true);
        _contacts.Apply(s.IsContactEnabled, Summary(s.ContactMode, s.ContactProviderOptionText), initial);

        _tasks.Provider!.Set(s.TaskProviderOptionText,
            s.IsTaskProviderModeAvailable ? s.TaskProviderOptionDescription : s.TaskLocalOnlyNote,
            s.IsTaskProviderSelected, s.IsTaskProviderModeAvailable);
        _tasks.Local!.Set(null, null, s.IsTaskLocalSelected, true);
        _tasks.Apply(s.IsTaskEnabled, Summary(s.TaskMode, s.TaskProviderOptionText), initial);
    }

    private static string Summary(AccountCapabilityMode mode, string providerText) => mode switch
    {
        AccountCapabilityMode.Provider => providerText,
        AccountCapabilityMode.Local => Translator.CapabilityPicker_LocalOption,
        _ => Translator.ProviderSelection_ModeOff
    };

    /// <summary>One capability: the expander, its summary and switch, and the mode radios.</summary>
    private sealed class CapabilityCard
    {
        private readonly NSTextField _summary;
        private readonly NSSwitch _switch;
        private bool? _lastEnabled;

        public CapabilityCard(string title, string description, WinoIconGlyph icon, Action<bool> toggle, OptionRow? provider, OptionRow? local)
        {
            Provider = provider;
            Local = local;
            _summary = WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.SecondaryText);
            _summary.Alignment = NSTextAlignment.Right;
            _summary.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
            _summary.WidthAnchor.ConstraintLessThanOrEqualTo(220).Active = true;
            _switch = new NSSwitch { TranslatesAutoresizingMaskIntoConstraints = false };
            WinoAccessibility.Label(_switch, title);
            _switch.Activated += (_, _) => toggle(_switch.State != 0);
            var trailing = WinoLayout.HStack(WinoStyle.Space3, _summary, _switch);

            Expander = new WinoSettingsExpander(title, description, icon, trailing);
            if (provider is not null && local is not null)
            {
                var options = WinoLayout.VStack(WinoStyle.Space3, provider, local);
                foreach (var row in options.ArrangedSubviews) row.WidthAnchor.ConstraintEqualTo(options.WidthAnchor).Active = true;
                Expander.Add(options, 12, WinoSettingsStyle.NestedIndent, 12, WinoSettingsStyle.CardPadding);
            }
        }

        public WinoSettingsExpander Expander { get; }
        public OptionRow? Provider { get; }
        public OptionRow? Local { get; }

        /// <summary>
        /// Follows the Windows IsExpanded binding: turning a capability on opens its card and
        /// turning it off closes it. In between the user can open or close the card freely.
        /// </summary>
        public void Apply(bool enabled, string summary, bool initial)
        {
            _switch.State = enabled ? 1 : 0;
            _summary.StringValue = summary;
            _summary.TextColor = enabled ? WinoStyle.SecondaryText : WinoStyle.TertiaryText;
            Provider?.SetGroupEnabled(enabled);
            Local?.SetGroupEnabled(enabled);
            if (_lastEnabled != enabled && Expander.HeaderCard.IsExpanderHeader)
            {
                if (initial || _lastEnabled is not null) Expander.IsExpanded = enabled;
            }
            _lastEnabled = enabled;
        }
    }

    /// <summary>A radio choice with its consequence underneath, indented to the radio title.</summary>
    private sealed class OptionRow : NSView
    {
        private readonly NSButton _radio;
        private readonly NSTextField _description;
        private bool _available = true;
        private bool _groupEnabled = true;

        public OptionRow(Action select, string? title = null, string? description = null)
        {
            TranslatesAutoresizingMaskIntoConstraints = false;
            _radio = new NSButton { Title = title ?? string.Empty, TranslatesAutoresizingMaskIntoConstraints = false };
            _radio.SetButtonType(NSButtonType.Radio);
            _radio.Activated += (_, _) => { if (_radio.State == NSCellStateValue.On) select(); };
            _description = WinoStyle.Label(description, WinoStyle.Description, WinoStyle.SecondaryText, 0);
            _description.PreferredMaxLayoutWidth = 420;
            AddSubview(_radio);
            AddSubview(_description);
            NSLayoutConstraint.ActivateConstraints(
            [
                _radio.TopAnchor.ConstraintEqualTo(TopAnchor),
                _radio.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
                _radio.TrailingAnchor.ConstraintLessThanOrEqualTo(TrailingAnchor),
                _description.TopAnchor.ConstraintEqualTo(_radio.BottomAnchor, 2),
                // The radio glyph is 16pt plus a 4pt gap: descriptions start under the title text.
                _description.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, 20),
                _description.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
                _description.BottomAnchor.ConstraintEqualTo(BottomAnchor)
            ]);
        }

        public void Set(string? title, string? description, bool selected, bool available)
        {
            if (title is not null) _radio.Title = title;
            if (description is not null) _description.StringValue = description;
            _radio.State = selected ? NSCellStateValue.On : NSCellStateValue.Off;
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
            _radio.Enabled = enabled;
            _description.TextColor = enabled ? WinoStyle.SecondaryText : WinoStyle.TertiaryText;
            WinoAccessibility.Help(_radio, _description.StringValue);
        }
    }
}
