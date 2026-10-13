using System.ComponentModel;
using System.Windows.Input;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Bindings;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Onboarding;

/// <summary>
/// Typed two-way bindings for UIKit form controls on the onboarding pages (the Mac FormBinder): text and secure text
/// fields, pull-down menus, switches, labels, buttons and visibility. Every binding is owned by the page's scope.
/// </summary>
public sealed class FormBinder<TSource>(TSource source, BindingScope scope, IDispatcher dispatcher, Action<Exception> error)
    where TSource : class, INotifyPropertyChanged
{
    public TSource Source => source;

    /// <summary>Editable fields in creation order; Return in a field moves to the next one.</summary>
    public List<UITextField> Fields { get; } = new();

    public PropertyBinding<TSource, TValue> Bind<TValue>(string property, Func<TSource, TValue> read, Action<TValue> apply)
        => scope.Own(new PropertyBinding<TSource, TValue>(source, property, read, apply, dispatcher, error));

    public UITextField Text(string property, Func<TSource, string?> read, Action<TSource, string> write, string? placeholder = null, string? accessibilityLabel = null)
        => Attach(new UITextField(), property, read, write, placeholder, accessibilityLabel);

    public UITextField Secure(string property, Func<TSource, string?> read, Action<TSource, string> write, string? accessibilityLabel = null)
    {
        var field = Attach(new UITextField(), property, read, write, null, accessibilityLabel);
        field.SecureTextEntry = true;
        field.TextContentType = UITextContentType.Password;
        return field;
    }

    private UITextField Attach(UITextField field, string property, Func<TSource, string?> read, Action<TSource, string> write, string? placeholder, string? accessibilityLabel)
    {
        field.TranslatesAutoresizingMaskIntoConstraints = false;
        field.Placeholder = placeholder ?? string.Empty;
        field.BorderStyle = UITextBorderStyle.RoundedRect;
        field.Font = WinoStyle.Body;
        field.AdjustsFontForContentSizeCategory = true;
        field.AutocorrectionType = UITextAutocorrectionType.No;
        field.AutocapitalizationType = UITextAutocapitalizationType.None;
        field.ClearButtonMode = UITextFieldViewMode.WhileEditing;
        field.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        if (accessibilityLabel is not null) field.AccessibilityLabel = accessibilityLabel;
        var binding = scope.Own(new PropertyBinding<TSource, string>(source, property, vm => read(vm) ?? string.Empty,
            value => { if (field.Text != value) field.Text = value; }, dispatcher, error, write));
        EventHandler changed = (_, _) => binding.UpdateSource(field.Text ?? string.Empty);
        field.EditingChanged += changed;
        scope.Own(new ActionDisposable(() => field.EditingChanged -= changed));
        var index = Fields.Count;
        field.ReturnKeyType = UIReturnKeyType.Next;
        field.ShouldReturn = _ =>
        {
            var next = Fields.Skip(index + 1).FirstOrDefault(candidate => !IsHidden(candidate));
            if (next is not null) next.BecomeFirstResponder();
            else field.ResignFirstResponder();
            return false;
        };
        Fields.Add(field);
        return field;
    }

    private static bool IsHidden(UIView view)
    {
        for (UIView? current = view; current is not null; current = current.Superview)
            if (current.Hidden) return true;
        return false;
    }

    /// <summary>A pull-down menu button over a fixed item list; the selection is the bound index.</summary>
    public UIButton PopUp(IReadOnlyList<string> items, string property, Func<TSource, int> read, Action<TSource, int> write, string? accessibilityLabel = null)
    {
        var popUp = new WinoPopUpButton(accessibilityLabel);
        var binding = scope.Own(new PropertyBinding<TSource, int>(source, property, read, index => popUp.SetItems(items, index), dispatcher, error, write));
        popUp.Chosen = index => binding.UpdateSource(index);
        popUp.SetItems(items, read(source));
        return popUp;
    }

    /// <summary>A switch row with its title; the switch is bound two-way.</summary>
    public UIView Check(string title, string property, Func<TSource, bool> read, Action<TSource, bool> write)
    {
        var toggle = new UISwitch { TranslatesAutoresizingMaskIntoConstraints = false };
        toggle.AccessibilityLabel = title;
        var binding = scope.Own(new PropertyBinding<TSource, bool>(source, property, read, value => toggle.On = value, dispatcher, error, write));
        EventHandler changed = (_, _) => binding.UpdateSource(toggle.On);
        toggle.ValueChanged += changed;
        scope.Own(new ActionDisposable(() => toggle.ValueChanged -= changed));
        var label = WinoStyle.Label(title, WinoStyle.Body, WinoStyle.PrimaryText, 0);
        var row = WinoLayout.HStack(WinoStyle.Space3, label, toggle);
        row.Alignment = UIStackViewAlignment.Center;
        return row;
    }

    public void Visible(UIView view, string property, Func<TSource, bool> read)
        => Bind(property, read, visible => view.Hidden = !visible);

    public void Label(UILabel label, string property, Func<TSource, string?> read)
        => Bind(property, read, value => label.Text = value ?? string.Empty);

    /// <summary>A button that runs a command and follows its CanExecute.</summary>
    public UIButton Command(string title, ICommand command, bool primary = false)
    {
        var button = SettingsBinder.CreateButton(title, primary);
        var binding = scope.Own(new CommandBinding(command, () => null, enabled => button.Enabled = enabled, dispatcher, error));
        EventHandler handler = (_, _) => binding.Execute();
        button.PrimaryActionTriggered += handler;
        scope.Own(new ActionDisposable(() => button.PrimaryActionTriggered -= handler));
        return button;
    }

    /// <summary>An accent-coloured link button (help links under a field).</summary>
    public UIButton Link(string title, ICommand command)
    {
        var button = UIButton.FromType(UIButtonType.System);
        button.TranslatesAutoresizingMaskIntoConstraints = false;
        button.SetTitle(title, UIControlState.Normal);
        button.TitleLabel.Font = WinoStyle.Description;
        button.HorizontalAlignment = UIControlContentHorizontalAlignment.Leading;
        var binding = scope.Own(new CommandBinding(command, () => null, enabled => button.Enabled = enabled, dispatcher, error));
        EventHandler handler = (_, _) => binding.Execute();
        button.PrimaryActionTriggered += handler;
        scope.Own(new ActionDisposable(() => button.PrimaryActionTriggered -= handler));
        return button;
    }

    /// <summary>
    /// Capability rows (Mail, Calendar, Contacts, To Do) as pull-down menus over an <see cref="AccountCapabilitySelection"/>
    /// (Mac FormBinder.CapabilityRows). A provider mode the provider cannot serve stays listed but disabled.
    /// </summary>
    public static IEnumerable<(string Label, UIView Control)> CapabilityRows(FormBinder<AccountCapabilitySelection> form, Func<string> mailProviderLabel)
    {
        var mailItems = new[] { string.Format(Translator.CapabilityPicker_SyncWith, mailProviderLabel()), Translator.ProviderSelection_Choice_Off };
        var mail = form.PopUp(mailItems, nameof(AccountCapabilitySelection.MailMode), s => s.MailMode == AccountCapabilityMode.Off ? 1 : 0,
            (s, index) => s.MailMode = index == 0 ? AccountCapabilityMode.Provider : AccountCapabilityMode.Off, Translator.ProviderSelection_MailStepTitle);
        yield return (Translator.ProviderSelection_MailStepTitle, mail);
        yield return (Translator.ProviderSelection_CalendarStepTitle, ModePopUp(form, nameof(AccountCapabilitySelection.CalendarMode), s => s.CalendarMode, (s, v) => s.CalendarMode = v,
            nameof(AccountCapabilitySelection.CalendarProviderOptionText), s => s.CalendarProviderOptionText,
            nameof(AccountCapabilitySelection.IsCalendarProviderModeAvailable), s => s.IsCalendarProviderModeAvailable, Translator.ProviderSelection_CalendarStepTitle));
        yield return (Translator.ProviderSelection_ContactsStepTitle, ModePopUp(form, nameof(AccountCapabilitySelection.ContactMode), s => s.ContactMode, (s, v) => s.ContactMode = v,
            nameof(AccountCapabilitySelection.ContactProviderOptionText), s => s.ContactProviderOptionText,
            nameof(AccountCapabilitySelection.IsContactProviderModeAvailable), s => s.IsContactProviderModeAvailable, Translator.ProviderSelection_ContactsStepTitle));
        yield return (Translator.ProviderSelection_TasksStepTitle, ModePopUp(form, nameof(AccountCapabilitySelection.TaskMode), s => s.TaskMode, (s, v) => s.TaskMode = v,
            nameof(AccountCapabilitySelection.TaskProviderOptionText), s => s.TaskProviderOptionText,
            nameof(AccountCapabilitySelection.IsTaskProviderModeAvailable), s => s.IsTaskProviderModeAvailable, Translator.ProviderSelection_TasksStepTitle));
    }

    private static readonly AccountCapabilityMode[] Modes = [AccountCapabilityMode.Provider, AccountCapabilityMode.Local, AccountCapabilityMode.Off];

    private static UIButton ModePopUp(FormBinder<AccountCapabilitySelection> form, string property,
        Func<AccountCapabilitySelection, AccountCapabilityMode> read, Action<AccountCapabilitySelection, AccountCapabilityMode> write,
        string providerTextProperty, Func<AccountCapabilitySelection, string> providerText,
        string availableProperty, Func<AccountCapabilitySelection, bool> available, string accessibilityLabel)
    {
        var popUp = new WinoPopUpButton(accessibilityLabel);
        string[] Items() => [providerText(form.Source), Translator.ProviderSelection_Choice_Local_Device, Translator.ProviderSelection_Choice_Off];
        void Refresh() => popUp.SetItems(Items(), Array.IndexOf(Modes, read(form.Source)), index => index != 0 || available(form.Source));
        form.Bind(property, s => Array.IndexOf(Modes, read(s)), _ => Refresh());
        popUp.Chosen = index => { if (index >= 0 && index < Modes.Length) write(form.Source, Modes[index]); };
        form.Bind(providerTextProperty, providerText, _ => Refresh());
        form.Bind(availableProperty, available, _ => Refresh());
        Refresh();
        return popUp;
    }
}

/// <summary>A button that shows its current choice and opens a menu of the choices (the AppKit NSPopUpButton).</summary>
public sealed class WinoPopUpButton : UIButton
{
    public WinoPopUpButton(string? accessibilityLabel = null) : base(UIButtonType.System)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        ShowsMenuAsPrimaryAction = true;
        ChangesSelectionAsPrimaryAction = true;
        var configuration = UIButtonConfiguration.GrayButtonConfiguration;
        configuration.Indicator = UIButtonConfigurationIndicator.Popup;
        Configuration = configuration;
        HorizontalAlignment = UIControlContentHorizontalAlignment.Leading;
        if (accessibilityLabel is not null) AccessibilityLabel = accessibilityLabel;
    }

    /// <summary>Called with the index the user picked.</summary>
    public Action<int>? Chosen { get; set; }

    /// <summary>Replaces the choices; <paramref name="enabled"/> disables single entries.</summary>
    public void SetItems(IReadOnlyList<string> items, int selected, Func<int, bool>? enabled = null)
    {
        var actions = new UIMenuElement[items.Count];
        for (int index = 0; index < items.Count; index++)
        {
            int chosen = index;
            var action = UIAction.Create(items[index], null, null, _ => Chosen?.Invoke(chosen));
            action.State = index == selected ? UIMenuElementState.On : UIMenuElementState.Off;
            if (enabled is not null && !enabled(index)) action.Attributes = UIMenuElementAttributes.Disabled;
            actions[index] = action;
        }
        Menu = UIMenu.Create(string.Empty, actions);
        Enabled = items.Count > 0;
    }
}

/// <summary>
/// An iOS form: rows with the label above the control, on a rounded group surface (the Mac FormGrid's role).
/// Rows can be hidden; a row without a label holds help text, a switch or a link.
/// </summary>
public sealed class FormGrid : UIStackView
{
    public FormGrid()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        Axis = UILayoutConstraintAxis.Vertical;
        Spacing = (nfloat)WinoStyle.Space3;
        Alignment = UIStackViewAlignment.Fill;
    }

    public UIView Row(string? label, UIView control) => Row(label is null ? null : FieldLabel(label), control);

    public UIView Row(UILabel? label, UIView control)
    {
        UIView row = label is null ? control : WinoLayout.VStack(4, label, control);
        if (row is UIStackView stack) stack.Alignment = UIStackViewAlignment.Fill;
        AddArrangedSubview(row);
        return row;
    }

    public static UILabel FieldLabel(string text) => WinoStyle.Label(text, WinoStyle.Subheadline, WinoStyle.SecondaryText, 0);

    /// <summary>Secondary helper text under a field.</summary>
    public static UILabel Help(string? text = null) => WinoStyle.Label(text ?? string.Empty, WinoStyle.Description, WinoStyle.SecondaryText, 0);
}

/// <summary>
/// Shell of the onboarding pages (the Mac OnboardingPageView): a scrolling column with the header and the form,
/// centred and capped in width on iPad, and a footer pinned above the safe area with status on the leading side and
/// the actions on the trailing side. The theme backdrop shows behind it.
/// </summary>
public sealed class OnboardingPageView : UIView
{
    private readonly UIScrollView _scroll = new() { TranslatesAutoresizingMaskIntoConstraints = false, KeyboardDismissMode = UIScrollViewKeyboardDismissMode.Interactive };

    public OnboardingPageView(UIView header, UIView content, UIView? footerLeading, UIView[] actions, UIView primary, double maxContentWidth = 620)
    {
        BackgroundColor = UIColor.SystemGroupedBackground;
        var column = WinoLayout.VStack(WinoStyle.Space6, header, content);
        column.Alignment = UIStackViewAlignment.Fill;

        AddSubview(_scroll);
        _scroll.AddSubview(column);
        var scrollContent = _scroll.ContentLayoutGuide;
        var frame = _scroll.FrameLayoutGuide;
        var preferredWidth = column.WidthAnchor.ConstraintEqualTo(frame.WidthAnchor, 1, -40);
        preferredWidth.Priority = 750;

        var footerViews = new List<UIView>();
        if (footerLeading is not null) footerViews.Add(footerLeading);
        footerViews.Add(WinoLayout.Spacer());
        footerViews.AddRange(actions);
        footerViews.Add(primary);
        var footer = WinoLayout.HStack(WinoStyle.Space3, footerViews.ToArray());
        footer.Alignment = UIStackViewAlignment.Center;
        var bar = new WinoBarBackdropView { TranslatesAutoresizingMaskIntoConstraints = false };
        AddSubview(bar);
        AddSubview(footer);
        var separator = new WinoSeparator();
        AddSubview(separator);

        var guide = SafeAreaLayoutGuide;
        NSLayoutConstraint.ActivateConstraints(
        [
            _scroll.TopAnchor.ConstraintEqualTo(TopAnchor),
            _scroll.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            _scroll.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            _scroll.BottomAnchor.ConstraintEqualTo(bar.TopAnchor),
            column.TopAnchor.ConstraintEqualTo(scrollContent.TopAnchor, 24),
            column.BottomAnchor.ConstraintEqualTo(scrollContent.BottomAnchor, -24),
            column.CenterXAnchor.ConstraintEqualTo(frame.CenterXAnchor),
            column.WidthAnchor.ConstraintLessThanOrEqualTo((nfloat)maxContentWidth),
            column.WidthAnchor.ConstraintLessThanOrEqualTo(frame.WidthAnchor, 1, -32),
            preferredWidth,
            bar.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            bar.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            bar.BottomAnchor.ConstraintEqualTo(BottomAnchor),
            separator.TopAnchor.ConstraintEqualTo(bar.TopAnchor),
            separator.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            separator.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            footer.TopAnchor.ConstraintEqualTo(bar.TopAnchor, 12),
            footer.LeadingAnchor.ConstraintEqualTo(guide.LeadingAnchor, 20),
            footer.TrailingAnchor.ConstraintEqualTo(guide.TrailingAnchor, -20),
            footer.BottomAnchor.ConstraintEqualTo(KeyboardLayoutGuide.TopAnchor, -12),
        ]);
    }

    /// <summary>A title and wrapping description, with an optional leading icon.</summary>
    public static UIView Header(UIView? icon, UILabel title, UILabel description)
    {
        title.Font = WinoStyle.PageTitle;
        title.TextColor = WinoStyle.PrimaryText;
        title.Lines = 0;
        description.Font = WinoStyle.Body;
        description.TextColor = WinoStyle.SecondaryText;
        description.Lines = 0;
        var text = WinoLayout.VStack(4, title, description);
        text.Alignment = UIStackViewAlignment.Fill;
        if (icon is null) return text;
        var row = WinoLayout.HStack(WinoStyle.Space4, icon, text);
        row.Alignment = UIStackViewAlignment.Center;
        return row;
    }

    /// <summary>A bold group heading followed by its rows on a rounded surface.</summary>
    public static UIStackView Group(string? heading, UIView body, UIView? accessory = null)
    {
        var surface = new WinoSurfaceView { Fill = WinoStyle.GroupFill, Stroke = WinoStyle.GroupStroke, CornerRadius = WinoStyle.GroupRadius };
        WinoLayout.Fill(body, surface, 16);
        var stack = WinoLayout.VStack(6);
        stack.Alignment = UIStackViewAlignment.Fill;
        if (heading is not null || accessory is not null)
        {
            var title = WinoStyle.Label(heading ?? string.Empty, WinoStyle.BodyStrong);
            stack.AddArrangedSubview(accessory is null ? title : WinoLayout.HStack(WinoStyle.Space2, title, WinoLayout.Spacer(), accessory));
        }
        stack.AddArrangedSubview(surface);
        return stack;
    }
}

/// <summary>The IMAP connection status: a dot (or spinner while testing) and its text (Mac ConnectionStatusView).</summary>
public sealed class ConnectionStatusView : UIStackView
{
    private readonly WinoSurfaceView _dot = new() { CornerRadius = 4 };
    private readonly UIActivityIndicatorView _spinner = new(UIActivityIndicatorViewStyle.Medium) { HidesWhenStopped = true };
    private readonly UILabel _text = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText);

    public ConnectionStatusView()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        Axis = UILayoutConstraintAxis.Horizontal;
        Spacing = 6;
        Alignment = UIStackViewAlignment.Center;
        WinoLayout.Size(_dot, 8, 8);
        AddArrangedSubview(_dot);
        AddArrangedSubview(_spinner);
        AddArrangedSubview(_text);
        State = ConnectionTestState.NotTested;
    }

    public ConnectionTestState State
    {
        set
        {
            _text.Text = value switch
            {
                ConnectionTestState.Testing => Translator.ImapSetup_StatusTesting,
                ConnectionTestState.Succeeded => Translator.ImapSetup_StatusConnected,
                ConnectionTestState.Failed => Translator.ImapSetup_StatusFailed,
                _ => Translator.ImapSetup_StatusNotTested
            };
            _dot.Fill = value switch
            {
                ConnectionTestState.Succeeded => WinoStyle.Success,
                ConnectionTestState.Failed => WinoStyle.Critical,
                _ => UIColor.TertiaryLabel
            };
            bool testing = value == ConnectionTestState.Testing;
            _dot.Hidden = testing;
            if (testing) _spinner.StartAnimating(); else _spinner.StopAnimating();
            IsAccessibilityElement = true;
            AccessibilityLabel = _text.Text;
        }
    }
}

/// <summary>The guided IMAP steps (features, sign in, servers) with the current one highlighted (Mac SetupStepIndicator).</summary>
public sealed class SetupStepIndicator : UIStackView
{
    private readonly Step _signIn;
    private readonly Step _servers;

    public SetupStepIndicator()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        Axis = UILayoutConstraintAxis.Horizontal;
        Spacing = 8;
        Alignment = UIStackViewAlignment.Center;
        var features = new Step("1", Translator.ImapSetup_StepFeatures);
        _signIn = new Step("2", Translator.ImapSetup_StepSignIn);
        _servers = new Step("3", Translator.ImapSetup_StepServers);
        features.Apply(done: true, current: false);
        AddArrangedSubview(features);
        AddArrangedSubview(_signIn);
        AddArrangedSubview(_servers);
        SetCurrent(ImapSetupStep.SignIn);
    }

    public void SetCurrent(ImapSetupStep step)
    {
        _signIn.Apply(done: step == ImapSetupStep.Servers, current: step == ImapSetupStep.SignIn);
        _servers.Apply(done: false, current: step == ImapSetupStep.Servers);
    }

    private sealed class Step : UIStackView
    {
        private readonly WinoSurfaceView _circle = new() { CornerRadius = 11 };
        private readonly UILabel _number;
        private readonly WinoIconView _check = new(WinoIconGlyph.Checkmark, 11, UIColor.White) { Colorful = false };
        private readonly UILabel _title;

        public Step(string number, string title)
        {
            TranslatesAutoresizingMaskIntoConstraints = false;
            Axis = UILayoutConstraintAxis.Horizontal;
            Spacing = 6;
            Alignment = UIStackViewAlignment.Center;
            WinoLayout.Size(_circle, 22, 22);
            _number = WinoStyle.Label(number, WinoStyle.CaptionStrong);
            foreach (var view in new UIView[] { _number, _check })
            {
                view.TranslatesAutoresizingMaskIntoConstraints = false;
                _circle.AddSubview(view);
                view.CenterXAnchor.ConstraintEqualTo(_circle.CenterXAnchor).Active = true;
                view.CenterYAnchor.ConstraintEqualTo(_circle.CenterYAnchor).Active = true;
            }
            _title = WinoStyle.Label(title, WinoStyle.Description);
            AddArrangedSubview(_circle);
            AddArrangedSubview(_title);
        }

        public void Apply(bool done, bool current)
        {
            bool accent = done || current;
            _circle.Fill = accent ? WinoStyle.Accent : WinoStyle.SubtleFill;
            _number.TextColor = accent ? UIColor.White : WinoStyle.SecondaryText;
            _number.Hidden = done;
            _check.Hidden = !done;
            _title.TextColor = current ? WinoStyle.PrimaryText : WinoStyle.SecondaryText;
            IsAccessibilityElement = true;
            AccessibilityLabel = _title.Text;
        }
    }
}
