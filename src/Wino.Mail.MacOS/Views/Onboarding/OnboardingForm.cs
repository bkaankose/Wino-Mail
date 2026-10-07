using System.ComponentModel;
using System.Windows.Input;
using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Onboarding;

/// <summary>
/// Typed two-way bindings for native form controls on the onboarding pages: text and secure text
/// fields, pop-up buttons, checkboxes, and visibility. Every binding is owned by the page's scope.
/// </summary>
internal sealed class FormBinder<TSource>(TSource source, BindingScope scope, IDispatcher dispatcher, Action<Exception> error)
    where TSource : class, INotifyPropertyChanged
{
    public TSource Source => source;

    /// <summary>All editable fields in creation order; pages use it for the keyboard loop.</summary>
    public List<NSView> KeyViews { get; } = new();

    public PropertyBinding<TSource, TValue> Bind<TValue>(string property, Func<TSource, TValue> read, Action<TValue> apply)
        => scope.Own(new PropertyBinding<TSource, TValue>(source, property, read, apply, dispatcher, error));

    public NSTextField Text(string property, Func<TSource, string?> read, Action<TSource, string> write, string? placeholder = null, string? accessibilityLabel = null)
        => Attach(new NSTextField(), property, read, write, placeholder, accessibilityLabel);

    public NSSecureTextField Secure(string property, Func<TSource, string?> read, Action<TSource, string> write, string? accessibilityLabel = null)
        => Attach(new NSSecureTextField(), property, read, write, null, accessibilityLabel);

    private T Attach<T>(T field, string property, Func<TSource, string?> read, Action<TSource, string> write, string? placeholder, string? accessibilityLabel) where T : NSTextField
    {
        field.TranslatesAutoresizingMaskIntoConstraints = false;
        field.PlaceholderString = placeholder ?? string.Empty;
        field.UsesSingleLineMode = true;
        field.LineBreakMode = NSLineBreakMode.TruncatingTail;
        field.Cell.Scrollable = true;
        field.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        field.WidthAnchor.ConstraintGreaterThanOrEqualTo(200).Active = true;
        if (accessibilityLabel is not null) field.AccessibilityLabel = accessibilityLabel;
        var binding = scope.Own(new PropertyBinding<TSource, string>(source, property, vm => read(vm) ?? string.Empty,
            value => { if (field.StringValue != value) field.StringValue = value; }, dispatcher, error, write));
        EventHandler changed = (_, _) => binding.UpdateSource(field.StringValue);
        field.Changed += changed;
        scope.Own(new ActionDisposable(() => field.Changed -= changed));
        KeyViews.Add(field);
        return field;
    }

    public NSPopUpButton PopUp(IEnumerable<string> items, string property, Func<TSource, int> read, Action<TSource, int> write, string? accessibilityLabel = null)
    {
        var popUp = new NSPopUpButton { TranslatesAutoresizingMaskIntoConstraints = false };
        popUp.AddItems(items.ToArray());
        if (accessibilityLabel is not null) WinoAccessibility.Label(popUp, accessibilityLabel);
        var binding = scope.Own(new PropertyBinding<TSource, int>(source, property, read,
            index => { if (index >= 0 && index < popUp.ItemCount) popUp.SelectItem(index); }, dispatcher, error, write));
        EventHandler activated = (_, _) => { if (popUp.IndexOfSelectedItem >= 0) binding.UpdateSource((int)popUp.IndexOfSelectedItem); };
        popUp.Activated += activated;
        scope.Own(new ActionDisposable(() => popUp.Activated -= activated));
        KeyViews.Add(popUp);
        return popUp;
    }

    public NSButton Check(string title, string property, Func<TSource, bool> read, Action<TSource, bool> write)
    {
        var box = WinoCheckbox.Create(title);
        var binding = scope.Own(new PropertyBinding<TSource, bool>(source, property, read,
            value => box.State = value ? NSCellStateValue.On : NSCellStateValue.Off, dispatcher, error, write));
        EventHandler activated = (_, _) => binding.UpdateSource(box.State == NSCellStateValue.On);
        box.Activated += activated;
        scope.Own(new ActionDisposable(() => box.Activated -= activated));
        KeyViews.Add(box);
        return box;
    }

    public void Visible(NSView view, string property, Func<TSource, bool> read)
        => Bind(property, read, visible => view.Hidden = !visible);

    public void Visible(NSGridRow row, string property, Func<TSource, bool> read)
        => Bind(property, read, visible => row.Hidden = !visible);

    public void Label(NSTextField label, string property, Func<TSource, string?> read)
        => Bind(property, read, value => label.StringValue = value ?? string.Empty);

    /// <summary>A push button that runs a command and follows its CanExecute.</summary>
    public NSButton Command(string title, ICommand command, NSBezelStyle style = NSBezelStyle.Rounded)
    {
        var button = new NSButton { Title = title, BezelStyle = style, TranslatesAutoresizingMaskIntoConstraints = false };
        var binding = scope.Own(new CommandBinding(command, () => null, enabled => button.Enabled = enabled, dispatcher, error));
        EventHandler handler = (_, _) => binding.Execute();
        button.Activated += handler;
        scope.Own(new ActionDisposable(() => button.Activated -= handler));
        return button;
    }

    /// <summary>A borderless accent-coloured link button (help links under a field).</summary>
    public NSButton Link(string title, ICommand command)
    {
        var button = Command(title, command, NSBezelStyle.Inline);
        button.Bordered = false;
        button.AttributedTitle = new Foundation.NSAttributedString(title, new NSStringAttributes
        {
            ForegroundColor = WinoStyle.Accent,
            Font = WinoStyle.Body,
            UnderlineStyle = (int)Foundation.NSUnderlineStyle.Single
        });
        return button;
    }

    /// <summary>
    /// Capability rows (Mail, Calendar, Contacts, To Do) as pop-up buttons over an
    /// <see cref="AccountCapabilitySelection"/>. A provider mode the provider cannot serve stays
    /// listed but disabled, like the Windows capability picker.
    /// </summary>
    public static IEnumerable<(string Label, NSView Control)> CapabilityRows(FormBinder<AccountCapabilitySelection> form, Func<string> mailProviderLabel)
    {
        var mail = form.PopUp([string.Format(Translator.CapabilityPicker_SyncWith, mailProviderLabel()), Translator.ProviderSelection_Choice_Off],
            nameof(AccountCapabilitySelection.MailMode), s => s.MailMode == AccountCapabilityMode.Off ? 1 : 0,
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

    private static NSPopUpButton ModePopUp(FormBinder<AccountCapabilitySelection> form, string property,
        Func<AccountCapabilitySelection, AccountCapabilityMode> read, Action<AccountCapabilitySelection, AccountCapabilityMode> write,
        string providerTextProperty, Func<AccountCapabilitySelection, string> providerText,
        string availableProperty, Func<AccountCapabilitySelection, bool> available, string accessibilityLabel)
    {
        var popUp = form.PopUp([providerText(form.Source), Translator.ProviderSelection_Choice_Local, Translator.ProviderSelection_Choice_Off],
            property, s => Array.IndexOf(Modes, read(s)), (s, index) => write(s, Modes[index]), accessibilityLabel);
        popUp.AutoEnablesItems = false;
        form.Bind(providerTextProperty, providerText, text => popUp.ItemAtIndex(0)!.Title = text);
        form.Bind(availableProperty, available, enabled => popUp.ItemAtIndex(0)!.Enabled = enabled);
        return popUp;
    }
}

/// <summary>
/// A Preferences-style form: right-aligned labels in a fixed column, controls filling the second.
/// Rows can be hidden; a row without a label puts its control in the control column.
/// </summary>
internal sealed class FormGrid : NSGridView
{
    public const double LabelWidth = 150;

    public FormGrid()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        RowSpacing = 10;
        ColumnSpacing = 12;
        RowAlignment = NSGridRowAlignment.FirstBaseline;
    }

    public NSGridRow Row(string? label, NSView control) => Row(label is null ? null : FieldLabel(label), control);

    public NSGridRow Row(NSTextField? label, NSView control)
    {
        var row = AddRow([label ?? (NSView)NSGridCell.EmptyContentView, control]);
        if (ColumnCount == 2 && RowCount == 1)
        {
            var labels = GetColumn(0);
            labels.X = NSGridCellPlacement.Trailing;
            labels.Width = (nfloat)LabelWidth;
            GetColumn(1).X = NSGridCellPlacement.Fill;
        }
        return row;
    }

    public static NSTextField FieldLabel(string text)
    {
        var label = WinoStyle.Label(text, WinoStyle.Body, WinoStyle.PrimaryText, 2);
        label.Alignment = NSTextAlignment.Right;
        label.PreferredMaxLayoutWidth = (nfloat)LabelWidth;
        return label;
    }

    /// <summary>Secondary helper text under a field, wrapping to the control column.</summary>
    public static NSTextField Help(string? text = null)
    {
        var help = WinoStyle.Label(text ?? string.Empty, WinoStyle.Description, WinoStyle.SecondaryText, 0);
        help.PreferredMaxLayoutWidth = 380;
        help.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        return help;
    }
}

/// <summary>
/// Shell of the onboarding form pages: a scrolling body with the header and form, and a pinned
/// footer with status on the leading side and the actions on the trailing side. The primary
/// action is the default button, so Return continues from any field.
/// </summary>
internal sealed class OnboardingPageView : NSView
{
    public OnboardingPageView(NSView header, NSView content, NSView footerLeading, NSButton[] actions, NSButton primary, double maxContentWidth = 620)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;

        var column = WinoLayout.VStack(WinoStyle.Space6, header, content);
        column.Alignment = NSLayoutAttribute.Leading;
        header.WidthAnchor.ConstraintEqualTo(column.WidthAnchor).Active = true;
        content.WidthAnchor.ConstraintEqualTo(column.WidthAnchor).Active = true;

        var document = new FlippedView();
        document.AddSubview(column);
        var preferredWidth = column.WidthAnchor.ConstraintEqualTo(document.WidthAnchor, 1, -96);
        preferredWidth.Priority = 750;
        NSLayoutConstraint.ActivateConstraints(
        [
            column.TopAnchor.ConstraintEqualTo(document.TopAnchor, 40),
            column.BottomAnchor.ConstraintEqualTo(document.BottomAnchor, -24),
            column.CenterXAnchor.ConstraintEqualTo(document.CenterXAnchor),
            column.WidthAnchor.ConstraintLessThanOrEqualTo((nfloat)maxContentWidth),
            preferredWidth
        ]);

        var scroll = new NSScrollView
        {
            HasVerticalScroller = true,
            AutohidesScrollers = true,
            DrawsBackground = false,
            TranslatesAutoresizingMaskIntoConstraints = false,
            DocumentView = document
        };
        document.WidthAnchor.ConstraintEqualTo(scroll.ContentView.WidthAnchor).Active = true;

        primary.KeyEquivalent = "\r";
        var footer = WinoLayout.HStack(WinoStyle.Space2, [footerLeading, WinoLayout.Spacer(), .. actions, primary]);
        footer.EdgeInsets = new NSEdgeInsets(14, 24, 18, 24);
        var separator = new WinoSeparator();

        AddSubview(scroll);
        AddSubview(separator);
        AddSubview(footer);
        NSLayoutConstraint.ActivateConstraints(
        [
            scroll.TopAnchor.ConstraintEqualTo(TopAnchor),
            scroll.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            scroll.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            scroll.BottomAnchor.ConstraintEqualTo(separator.TopAnchor),
            separator.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            separator.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            separator.BottomAnchor.ConstraintEqualTo(footer.TopAnchor),
            footer.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            footer.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            footer.BottomAnchor.ConstraintEqualTo(BottomAnchor)
        ]);
    }

    /// <summary>A title and wrapping description stacked like the other onboarding pages.</summary>
    public static NSStackView Header(NSView? icon, NSTextField title, NSTextField description)
    {
        title.Font = WinoStyle.PageTitle;
        title.TextColor = WinoStyle.PrimaryText;
        description.Font = WinoStyle.Body;
        description.TextColor = WinoStyle.SecondaryText;
        description.MaximumNumberOfLines = 0;
        description.LineBreakMode = NSLineBreakMode.ByWordWrapping;
        description.PreferredMaxLayoutWidth = 560;
        description.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        var text = WinoLayout.VStack(4, title, description);
        text.Alignment = NSLayoutAttribute.Leading;
        if (icon is null) return text;
        var row = WinoLayout.HStack(WinoStyle.Space4, icon, text);
        row.Alignment = NSLayoutAttribute.CenterY;
        return row;
    }

    /// <summary>A bold group heading followed by its rows on a rounded surface.</summary>
    public static NSStackView Group(string? heading, NSView body, NSView? accessory = null)
    {
        var inner = WinoLayout.VStack(0, body);
        inner.EdgeInsets = new NSEdgeInsets(16, 16, 16, 16);
        body.WidthAnchor.ConstraintEqualTo(inner.WidthAnchor, 1, -32).Active = true;
        var surface = new WinoSurfaceView { Fill = WinoStyle.GroupFill, Stroke = WinoStyle.GroupStroke, CornerRadius = WinoStyle.GroupRadius };
        WinoLayout.Fill(inner, surface);

        var stack = WinoLayout.VStack(6);
        stack.Alignment = NSLayoutAttribute.Leading;
        if (heading is not null || accessory is not null)
        {
            var title = WinoStyle.Label(heading ?? string.Empty, WinoStyle.BodyStrong);
            var row = accessory is null ? WinoLayout.HStack(0, title) : WinoLayout.HStack(WinoStyle.Space2, title, WinoLayout.Spacer(), accessory);
            stack.AddArrangedSubview(row);
            row.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
        }
        stack.AddArrangedSubview(surface);
        surface.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
        return stack;
    }

    /// <summary>
    /// Chains the editable views into the window's keyboard loop in form order. Hidden views stay
    /// in the chain; AppKit skips views that cannot become key, so rows can appear later.
    /// </summary>
    public static void ChainKeyViews(IReadOnlyList<NSView> views, NSButton? last = null)
    {
        var chain = views.ToList();
        if (last is not null) chain.Add(last);
        for (int index = 0; index < chain.Count; index++)
            chain[index].NextKeyView = chain[(index + 1) % chain.Count];
    }

    private sealed class FlippedView : NSView
    {
        public FlippedView() => TranslatesAutoresizingMaskIntoConstraints = false;
        public override bool IsFlipped => true;
    }
}
