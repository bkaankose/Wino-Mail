using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;
using Wino.Mail.Controls.Core.ContextFlyout;

namespace Wino.Mail.Controls.ContextFlyout;

/// <summary>
/// Base definition for items declared inside a <see cref="WinoContextFlyout"/> in XAML.
/// </summary>
public abstract partial class WinoContextFlyoutItemBase : DependencyObject
{
    protected internal abstract ContextFlyoutMenuEntry CreateEntry();
}

/// <summary>
/// A command item declared directly in XAML.
/// </summary>
public partial class WinoContextFlyoutItem : WinoContextFlyoutItemBase
{
    public event EventHandler? Click;

    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string Text { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string SearchKeywords { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string IconGlyph { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string IconForegroundHex { get; set; }

    [GeneratedDependencyProperty]
    public partial ICommand? Command { get; set; }

    [GeneratedDependencyProperty]
    public partial object? CommandParameter { get; set; }

    [GeneratedDependencyProperty(DefaultValue = true)]
    public partial bool IsEnabled { get; set; }

    [GeneratedDependencyProperty(DefaultValue = false)]
    public partial bool IsDestructive { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string ShortcutText { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string ShortcutKey { get; set; }

    [GeneratedDependencyProperty(DefaultValue = false)]
    public partial bool ShortcutControl { get; set; }

    [GeneratedDependencyProperty(DefaultValue = false)]
    public partial bool ShortcutAlt { get; set; }

    [GeneratedDependencyProperty(DefaultValue = false)]
    public partial bool ShortcutShift { get; set; }

    [GeneratedDependencyProperty(DefaultValue = false)]
    public partial bool ShortcutWindows { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string AutomationId { get; set; }

    protected internal override ContextFlyoutMenuEntry CreateEntry() => CreateCommandEntry();

    protected virtual ContextFlyoutCommandEntry CreateCommandEntry() => new()
    {
        Text = Text,
        SearchKeywords = SearchKeywords,
        Icon = CreateIcon(),
        Command = CreateEffectiveCommand(),
        CommandParameter = CommandParameter,
        IsEnabled = IsEnabled,
        IsDestructive = IsDestructive,
        Shortcut = CreateShortcut(),
        AutomationId = AutomationId
    };

    protected ContextFlyoutIcon? CreateIcon()
        => string.IsNullOrEmpty(IconGlyph)
            ? null
            : new ContextFlyoutIcon(IconGlyph, string.IsNullOrWhiteSpace(IconForegroundHex) ? null : IconForegroundHex);

    protected ContextFlyoutShortcut? CreateShortcut()
        => string.IsNullOrWhiteSpace(ShortcutText)
            ? null
            : new ContextFlyoutShortcut(
                ShortcutText,
                ShortcutKey,
                ShortcutControl,
                ShortcutAlt,
                ShortcutShift,
                ShortcutWindows);

    protected ICommand? CreateEffectiveCommand()
        => Command ?? (Click is null ? null : new ContextFlyoutClickCommand(this));

    protected sealed partial class ContextFlyoutClickCommand(WinoContextFlyoutItem owner) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => owner.IsEnabled;

        public void Execute(object? parameter) => owner.Click?.Invoke(owner, EventArgs.Empty);
    }
}

public sealed partial class WinoContextFlyoutToggleItem : WinoContextFlyoutItem
{
    [GeneratedDependencyProperty(DefaultValue = false)]
    public partial bool IsChecked { get; set; }

    protected override ContextFlyoutCommandEntry CreateCommandEntry() => new ContextFlyoutToggleEntry
    {
        Text = Text,
        SearchKeywords = SearchKeywords,
        Icon = CreateIcon(),
        Command = CreateEffectiveCommand(),
        CommandParameter = CommandParameter,
        IsEnabled = IsEnabled,
        IsDestructive = IsDestructive,
        Shortcut = CreateShortcut(),
        AutomationId = AutomationId,
        IsChecked = IsChecked
    };
}

public sealed partial class WinoContextFlyoutRadioItem : WinoContextFlyoutItem
{
    [GeneratedDependencyProperty(DefaultValue = false)]
    public partial bool IsChecked { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string GroupName { get; set; }

    protected override ContextFlyoutCommandEntry CreateCommandEntry() => new ContextFlyoutRadioEntry
    {
        Text = Text,
        SearchKeywords = SearchKeywords,
        Icon = CreateIcon(),
        Command = CreateEffectiveCommand(),
        CommandParameter = CommandParameter,
        IsEnabled = IsEnabled,
        IsDestructive = IsDestructive,
        Shortcut = CreateShortcut(),
        AutomationId = AutomationId,
        IsChecked = IsChecked,
        GroupName = GroupName
    };
}

public sealed partial class WinoContextFlyoutSeparator : WinoContextFlyoutItemBase
{
    protected internal override ContextFlyoutMenuEntry CreateEntry() => ContextFlyoutSeparatorEntry.Instance;
}

[ContentProperty(Name = nameof(Items))]
public sealed partial class WinoContextFlyoutSubItem : WinoContextFlyoutItemBase
{
    public ObservableCollection<WinoContextFlyoutItemBase> Items { get; } = [];

    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string Text { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string SearchKeywords { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string IconGlyph { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string IconForegroundHex { get; set; }

    [GeneratedDependencyProperty(DefaultValue = true)]
    public partial bool IsEnabled { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string AutomationId { get; set; }

    protected internal override ContextFlyoutMenuEntry CreateEntry() => new ContextFlyoutSubMenuEntry
    {
        Text = Text,
        SearchKeywords = SearchKeywords,
        Icon = string.IsNullOrEmpty(IconGlyph)
            ? null
            : new ContextFlyoutIcon(IconGlyph, string.IsNullOrWhiteSpace(IconForegroundHex) ? null : IconForegroundHex),
        IsEnabled = IsEnabled,
        AutomationId = AutomationId,
        Items = Items.Select(static item => item.CreateEntry()).ToArray()
    };
}
