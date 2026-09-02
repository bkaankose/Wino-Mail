using System.Collections;
using System.Collections.ObjectModel;
using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Wino.Mail.Controls.ContextFlyout;

[ContentProperty(Name = nameof(Items))]
public partial class WinoContextFlyout : FlyoutBase
{
    private WinoContextFlyoutPresenter? _presenter;

    public WinoContextFlyout()
    {
        Items = [];
        Opened += OnOpened;
    }

    public ObservableCollection<DependencyObject> Items { get; }

    [GeneratedDependencyProperty]
    public partial IEnumerable? ItemsSource { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "Search commands…")]
    public partial string SearchPlaceholderText { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "No commands found")]
    public partial string NoResultsText { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string Language { get; set; }

    protected override Control CreatePresenter()
    {
        _presenter = new WinoContextFlyoutPresenter(this);
        return _presenter;
    }

    internal IReadOnlyList<WinoContextFlyoutItemBase> BuildFlatItems()
    {
        if (ItemsSource is not null && Items.Count > 0)
        {
            throw new InvalidOperationException($"{nameof(Items)} and {nameof(ItemsSource)} cannot both be set.");
        }

        var source = ItemsSource?.Cast<object>() ?? Items.Cast<object>();
        var result = new List<WinoContextFlyoutItemBase>();

        Flatten(source, string.Empty, result);
        return NormalizeSeparators(result);
    }

    internal void Invoke(WinoContextFlyoutItem item, IReadOnlyList<WinoContextFlyoutItemBase> allItems)
    {
        if (!item.IsEnabled || item.Command?.CanExecute(item.CommandParameter) != true)
        {
            return;
        }

        if (item is WinoContextFlyoutRadioItem radioItem)
        {
            foreach (var candidate in allItems.OfType<WinoContextFlyoutRadioItem>()
                         .Where(candidate => string.Equals(candidate.GroupName, radioItem.GroupName, StringComparison.Ordinal)))
            {
                candidate.IsChecked = ReferenceEquals(candidate, radioItem);
            }
        }
        else if (item is WinoContextFlyoutToggleItem toggleItem)
        {
            toggleItem.IsChecked = !toggleItem.IsChecked;
        }

        item.BeforeExecute?.Invoke();
        item.Command.Execute(item.CommandParameter);
        Hide();
    }

    private void OnOpened(object? sender, object e) => _presenter?.PrepareForOpen();

    private static void Flatten(IEnumerable source, string breadcrumb, ICollection<WinoContextFlyoutItemBase> destination)
    {
        var sourceItems = source.Cast<object>().ToList();

        foreach (var sourceItem in sourceItems)
        {
            switch (sourceItem)
            {
                case null:
                    continue;
                case WinoContextFlyoutItemBase item:
                    ApplyShortcutText(item);
                    destination.Add(item);
                    break;
                case MenuFlyoutSeparator:
                    destination.Add(new WinoContextFlyoutSeparator());
                    break;
                case SplitMenuFlyoutItem splitItem:
                    AddNativeLeaf(splitItem, breadcrumb, destination);
                    Flatten(splitItem.Items, AppendBreadcrumb(breadcrumb, splitItem.Text), destination);
                    break;
                case MenuFlyoutSubItem subItem:
                    Flatten(subItem.Items, AppendBreadcrumb(breadcrumb, subItem.Text), destination);
                    break;
                case RadioMenuFlyoutItem radioItem:
                    destination.Add(CreateNativeRadioItem(radioItem, breadcrumb, sourceItems));
                    break;
                case ToggleMenuFlyoutItem toggleItem:
                    destination.Add(CreateNativeToggleItem(toggleItem, breadcrumb));
                    break;
                case MenuFlyoutItem menuItem:
                    AddNativeLeaf(menuItem, breadcrumb, destination);
                    break;
                default:
                    throw new NotSupportedException($"Unsupported context flyout item type: {sourceItem.GetType().FullName}");
            }
        }
    }

    private static void AddNativeLeaf(MenuFlyoutItem item, string breadcrumb, ICollection<WinoContextFlyoutItemBase> destination)
    {
        EnsureNativeCommand(item);
        destination.Add(CreateNativeItem(item, breadcrumb));
    }

    private static WinoContextFlyoutItem CreateNativeItem(MenuFlyoutItem item, string breadcrumb)
        => ApplyNativeProperties(new WinoContextFlyoutItem(), item, breadcrumb);

    private static WinoContextFlyoutToggleItem CreateNativeToggleItem(ToggleMenuFlyoutItem item, string breadcrumb)
        => ApplyNativeProperties(new WinoContextFlyoutToggleItem
        {
            IsChecked = item.IsChecked,
            BeforeExecute = () => item.IsChecked = !item.IsChecked
        }, item, breadcrumb);

    private static WinoContextFlyoutRadioItem CreateNativeRadioItem(
        RadioMenuFlyoutItem item,
        string breadcrumb,
        IReadOnlyList<object> siblings)
        => ApplyNativeProperties(new WinoContextFlyoutRadioItem
        {
            IsChecked = item.IsChecked,
            GroupName = item.GroupName,
            BeforeExecute = () =>
            {
                foreach (var sibling in siblings.OfType<RadioMenuFlyoutItem>()
                             .Where(candidate => string.Equals(candidate.GroupName, item.GroupName, StringComparison.Ordinal)))
                {
                    sibling.IsChecked = ReferenceEquals(sibling, item);
                }
            }
        }, item, breadcrumb);

    private static T ApplyNativeProperties<T>(T target, MenuFlyoutItem source, string breadcrumb)
        where T : WinoContextFlyoutItem
    {
        EnsureNativeCommand(source);

        target.Text = string.IsNullOrWhiteSpace(breadcrumb)
            ? source.Text
            : $"{breadcrumb} › {source.Text}";
        target.Breadcrumb = breadcrumb;
        target.IconSource = CloneIconSource(source.Icon);
        target.Command = source.Command;
        target.CommandParameter = source.CommandParameter;
        target.IsEnabled = source.IsEnabled;
        target.ShortcutText = string.IsNullOrWhiteSpace(source.KeyboardAcceleratorTextOverride)
            ? FormatKeyboardAccelerator(source.KeyboardAccelerators.FirstOrDefault())
            : source.KeyboardAcceleratorTextOverride;
        target.AutomationId = AutomationProperties.GetAutomationId(source);
        target.KeyboardAccelerator = CloneKeyboardAccelerator(source.KeyboardAccelerators.FirstOrDefault());
        return target;
    }

    private static void ApplyShortcutText(WinoContextFlyoutItemBase item)
    {
        if (item is WinoContextFlyoutItem command && string.IsNullOrWhiteSpace(command.ShortcutText))
        {
            command.ShortcutText = FormatKeyboardAccelerator(command.KeyboardAccelerator);
        }
    }

    private static string FormatKeyboardAccelerator(KeyboardAccelerator? accelerator)
    {
        if (accelerator is null)
        {
            return string.Empty;
        }

        var parts = new List<string>();

        if (accelerator.Modifiers.HasFlag(VirtualKeyModifiers.Control)) parts.Add("Ctrl");
        if (accelerator.Modifiers.HasFlag(VirtualKeyModifiers.Menu)) parts.Add("Alt");
        if (accelerator.Modifiers.HasFlag(VirtualKeyModifiers.Shift)) parts.Add("Shift");
        if (accelerator.Modifiers.HasFlag(VirtualKeyModifiers.Windows)) parts.Add("Win");
        parts.Add(accelerator.Key.ToString());

        return string.Join("+", parts);
    }

    private static KeyboardAccelerator? CloneKeyboardAccelerator(KeyboardAccelerator? source)
        => source is null
            ? null
            : new KeyboardAccelerator
            {
                IsEnabled = source.IsEnabled,
                Key = source.Key,
                Modifiers = source.Modifiers
            };

    private static void EnsureNativeCommand(MenuFlyoutItem item)
    {
        if (item.IsEnabled && item.Command is null)
        {
            throw new InvalidOperationException(
                $"Enabled native menu item '{item.Text}' must use Command to be adapted by {nameof(WinoContextFlyout)}.");
        }
    }

    private static IconSource? CloneIconSource(IconElement? icon)
        => icon switch
        {
            FontIcon font => new FontIconSource
            {
                FontFamily = font.FontFamily,
                FontSize = font.FontSize,
                FontStyle = font.FontStyle,
                FontWeight = font.FontWeight,
                Foreground = font.Foreground,
                Glyph = font.Glyph
            },
            SymbolIcon symbol => new SymbolIconSource { Symbol = symbol.Symbol, Foreground = symbol.Foreground },
            PathIcon path => new PathIconSource { Data = path.Data, Foreground = path.Foreground },
            BitmapIcon bitmap => new BitmapIconSource
            {
                ShowAsMonochrome = bitmap.ShowAsMonochrome,
                UriSource = bitmap.UriSource,
                Foreground = bitmap.Foreground
            },
            _ => null
        };

    private static string AppendBreadcrumb(string breadcrumb, string text)
        => string.IsNullOrWhiteSpace(breadcrumb) ? text : $"{breadcrumb} › {text}";

    private static IReadOnlyList<WinoContextFlyoutItemBase> NormalizeSeparators(
        IEnumerable<WinoContextFlyoutItemBase> source)
    {
        var result = new List<WinoContextFlyoutItemBase>();

        foreach (var item in source)
        {
            if (item is WinoContextFlyoutSeparator)
            {
                if (result.Count == 0 || result[^1] is WinoContextFlyoutSeparator)
                {
                    continue;
                }
            }

            result.Add(item);
        }

        if (result.LastOrDefault() is WinoContextFlyoutSeparator)
        {
            result.RemoveAt(result.Count - 1);
        }

        return result;
    }
}
