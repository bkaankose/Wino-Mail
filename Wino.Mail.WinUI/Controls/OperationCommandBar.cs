using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.WinUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.Menus;
using Wino.Helpers;

namespace Wino.Mail.WinUI.Controls;

public sealed partial class OperationCommandBar : CommandBar
{
    private const string MailOperationTemplateKey = "OperationCommandBarMailOperationTemplate";
    private const string FolderOperationTemplateKey = "OperationCommandBarFolderOperationTemplate";
    private const string AIActionsTemplateKey = "OperationCommandBarAIActionsTemplate";
    private const string PopOutTemplateKey = "OperationCommandBarThemeToggleTemplate";
    private const string ThemeToggleTemplateKey = "OperationCommandBarThemeToggleTemplate";
    private const string SeparatorTemplateKey = "OperationCommandBarSeparatorTemplate";

    private readonly IPreferencesService? _preferencesService;
    private readonly HashSet<INotifyPropertyChanged> _trackedMenuItems = [];

    [GeneratedDependencyProperty]
    public partial ObservableCollection<IMenuOperation>? MenuItems { get; set; }

    [GeneratedDependencyProperty]
    public partial ICommand? ItemInvokedCommand { get; set; }

    [GeneratedDependencyProperty]
    public partial bool IsAIActionsEnabled { get; set; }

    [GeneratedDependencyProperty]
    public partial bool IsAIActionsPaneToggleVisible { get; set; }

    [GeneratedDependencyProperty]
    public partial bool IsEditorThemeDark { get; set; }

    [GeneratedDependencyProperty]
    public partial bool IsEditorThemeToggleVisible { get; set; }

    [GeneratedDependencyProperty]
    public partial bool IsPopOutButtonVisible { get; set; }

    public event EventHandler<bool>? AIActionsEnabledChanged;
    public event EventHandler? PopOutClicked;

    public OperationCommandBar()
    {
        _preferencesService = App.Current.Services.GetService<IPreferencesService>();

        DefaultLabelPosition = CommandBarDefaultLabelPosition.Right;
        IsDynamicOverflowEnabled = true;
        OverflowButtonVisibility = CommandBarOverflowButtonVisibility.Auto;

        Loaded += OnLoaded;
        DynamicOverflowItemsChanging += OperationCommandBar_DynamicOverflowItemsChanging;
    }

    partial void OnMenuItemsPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is INotifyCollectionChanged oldCollection)
        {
            oldCollection.CollectionChanged -= MenuItems_CollectionChanged;
        }

        DetachTrackedMenuItemHandlers();

        if (e.NewValue is ObservableCollection<IMenuOperation> newItems)
        {
            newItems.CollectionChanged += MenuItems_CollectionChanged;
            TrackMenuItemHandlers((IEnumerable<IMenuOperation>)newItems);
        }

        RefreshCommands();
    }

    partial void OnIsAIActionsEnabledChanged(bool newValue)
    {
        AIActionsEnabledChanged?.Invoke(this, newValue);
    }

    partial void OnIsAIActionsPaneToggleVisibleChanged(bool newValue)
    {
        RefreshCommands();
    }

    partial void OnIsEditorThemeDarkChanged(bool newValue)
    {
        RefreshCommands();
    }

    partial void OnIsEditorThemeToggleVisibleChanged(bool newValue)
    {
        RefreshCommands();
    }

    partial void OnIsPopOutButtonVisibleChanged(bool newValue)
    {
        RefreshCommands();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshCommands();
    }

    private void OperationCommandBar_DynamicOverflowItemsChanging(CommandBar sender, DynamicOverflowItemsChangingEventArgs args)
    {
        if (args.Action == CommandBarDynamicOverflowAction.AddingToOverflow || sender.SecondaryCommands.Count > 0)
        {
            sender.OverflowButtonVisibility = CommandBarOverflowButtonVisibility.Visible;
        }
        else
        {
            sender.OverflowButtonVisibility = CommandBarOverflowButtonVisibility.Collapsed;
        }
    }

    private void MenuItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            DetachTrackedMenuItemHandlers();

            if (sender is IEnumerable<IMenuOperation> refreshedItems)
            {
                TrackMenuItemHandlers(refreshedItems);
            }
        }
        else
        {
            UntrackMenuItemHandlers(e.OldItems);
            TrackMenuItemHandlers(e.NewItems);
        }

        RefreshCommands();
    }

    private void MenuItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName)
            || e.PropertyName == nameof(IMenuOperation.IsEnabled)
            || e.PropertyName == nameof(IMenuOperation.IsSecondaryMenuPreferred)
            || e.PropertyName == nameof(MenuOperationItemBase<MailOperation>.Operation)
            || e.PropertyName == nameof(MenuOperationItemBase<MailOperation>.Identifier))
        {
            RefreshCommands();
        }
    }

    private void TrackMenuItemHandlers(IEnumerable<IMenuOperation> items)
    {
        foreach (var item in items)
        {
            if (item is INotifyPropertyChanged propertyChanged && _trackedMenuItems.Add(propertyChanged))
            {
                propertyChanged.PropertyChanged += MenuItem_PropertyChanged;
            }
        }
    }

    private void TrackMenuItemHandlers(System.Collections.IList? items)
    {
        if (items == null)
        {
            return;
        }

        foreach (var item in items)
        {
            if (item is IMenuOperation menuItem)
            {
                TrackMenuItemHandlers([menuItem]);
            }
        }
    }

    private void UntrackMenuItemHandlers(System.Collections.IList? items)
    {
        if (items == null)
        {
            return;
        }

        foreach (var item in items)
        {
            if (item is INotifyPropertyChanged propertyChanged && _trackedMenuItems.Remove(propertyChanged))
            {
                propertyChanged.PropertyChanged -= MenuItem_PropertyChanged;
            }
        }
    }

    private void DetachTrackedMenuItemHandlers()
    {
        foreach (var item in _trackedMenuItems)
        {
            item.PropertyChanged -= MenuItem_PropertyChanged;
        }

        _trackedMenuItems.Clear();
    }

    private void RefreshCommands()
    {
        ClearGeneratedCommands();

        if (IsAIActionsPaneToggleVisible)
        {
            PrimaryCommands.Add(CreateAIActionsToggleButton());
        }

        if (IsPopOutButtonVisible)
        {
            PrimaryCommands.Add(CreatePopOutButton());
        }

        if (IsEditorThemeToggleVisible)
        {
            PrimaryCommands.Add(CreateThemeToggleButton());
        }

        if (MenuItems == null)
        {
            UpdateOverflowButtonVisibility();
            return;
        }

        foreach (var item in MenuItems)
        {
            var element = CreateCommandElement(item);
            if (element == null)
            {
                continue;
            }

            if (item.IsSecondaryMenuPreferred)
            {
                SecondaryCommands.Add(element);
            }
            else
            {
                PrimaryCommands.Add(element);
            }
        }

        UpdateOverflowButtonVisibility();
    }

    private void ClearGeneratedCommands()
    {
        DetachCommandHandlers(PrimaryCommands);
        DetachCommandHandlers(SecondaryCommands);

        PrimaryCommands.Clear();
        SecondaryCommands.Clear();
    }

    private void DetachCommandHandlers(IEnumerable<ICommandBarElement> commands)
    {
        foreach (var command in commands)
        {
            switch (command)
            {
                case AppBarButton button:
                    button.Click -= OperationButton_Click;
                    button.Click -= ThemeButton_Click;
                    button.Click -= PopOutButton_Click;
                    break;
                case AppBarToggleButton toggleButton:
                    toggleButton.ClearValue(AppBarToggleButton.IsCheckedProperty);
                    break;
            }
        }
    }

    private ICommandBarElement? CreateCommandElement(IMenuOperation item)
    {
        if (item is MailOperationMenuItem mailOperation && mailOperation.Operation == MailOperation.Seperator)
        {
            return LoadCommandBarElementTemplate(SeparatorTemplateKey, new SeparatorCommandBarItemViewModel());
        }

        if (item is MailOperationMenuItem mailOperationItem)
        {
            var button = LoadCommandBarElementTemplate(
                MailOperationTemplateKey,
                new OperationCommandBarMenuOperationItemViewModel(
                    mailOperationItem,
                    XamlHelpers.GetOperationString(mailOperationItem.Operation),
                    XamlHelpers.GetWinoIconGlyph(mailOperationItem.Operation),
                    GetOperationLabelPosition(XamlHelpers.GetOperationString(mailOperationItem.Operation))))
                as AppBarButton;

            if (button == null)
            {
                return null;
            }

            button.Tag = mailOperationItem;
            button.Click += OperationButton_Click;
            return button;
        }

        if (item is FolderOperationMenuItem folderOperationItem)
        {
            var label = XamlHelpers.GetOperationString(folderOperationItem.Operation);
            var button = LoadCommandBarElementTemplate(
                FolderOperationTemplateKey,
                new OperationCommandBarMenuOperationItemViewModel(
                    folderOperationItem,
                    label,
                    XamlHelpers.GetPathGeometry(folderOperationItem.Operation),
                    GetOperationLabelPosition(label)))
                as AppBarButton;

            if (button == null)
            {
                return null;
            }

            button.Tag = folderOperationItem;
            button.Click += OperationButton_Click;
            return button;
        }

        return null;
    }

    private AppBarToggleButton CreateAIActionsToggleButton()
    {
        var button = (AppBarToggleButton)LoadCommandBarElementTemplate(
            AIActionsTemplateKey,
            new OperationCommandBarAIActionsItemViewModel(Translator.Composer_AiActions, "\uE945"));

        button.SetBinding(AppBarToggleButton.IsCheckedProperty, new Binding
        {
            Mode = BindingMode.TwoWay,
            Path = new PropertyPath(nameof(IsAIActionsEnabled)),
            Source = this
        });

        return button;
    }

    private AppBarButton CreateThemeToggleButton()
    {
        var label = IsEditorThemeDark ? Translator.Composer_LightTheme : Translator.Composer_DarkTheme;
        var icon = IsEditorThemeDark ? WinoIconGlyph.LightEditor : WinoIconGlyph.DarkEditor;

        var button = (AppBarButton)LoadCommandBarElementTemplate(
            ThemeToggleTemplateKey,
            new OperationCommandBarThemeItemViewModel(label, icon));

        button.Click += ThemeButton_Click;
        return button;
    }

    private AppBarButton CreatePopOutButton()
    {
        var button = (AppBarButton)LoadCommandBarElementTemplate(
            PopOutTemplateKey,
            new OperationCommandBarThemeItemViewModel(Translator.Buttons_PopOut, WinoIconGlyph.OpenInNewWindow));

        button.Click += PopOutButton_Click;
        return button;
    }

    private void OperationButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is AppBarButton button && button.Tag is IMenuOperation operation)
        {
            ItemInvokedCommand?.Execute(operation);
        }
    }

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        IsEditorThemeDark = !IsEditorThemeDark;
    }

    private void PopOutButton_Click(object sender, RoutedEventArgs e)
    {
        PopOutClicked?.Invoke(this, EventArgs.Empty);
    }

    private object? FindTemplateResource(string key)
    {
        if (TryGetResourceRecursive(Resources, key, out var resource))
        {
            return resource;
        }

        return TryGetResourceRecursive(Application.Current.Resources, key, out resource) ? resource : null;
    }

    private static bool TryGetResourceRecursive(ResourceDictionary dictionary, string key, out object? resource)
    {
        if (dictionary.TryGetValue(key, out resource))
        {
            return true;
        }

        foreach (var mergedDictionary in dictionary.MergedDictionaries)
        {
            if (TryGetResourceRecursive(mergedDictionary, key, out resource))
            {
                return true;
            }
        }

        resource = null;
        return false;
    }

    private ICommandBarElement LoadCommandBarElementTemplate(string resourceKey, object dataContext)
    {
        var template = FindTemplateResource(resourceKey) as DataTemplate
                       ?? throw new InvalidOperationException($"Unable to resolve resource '{resourceKey}'.");

        if (template.LoadContent() is not ICommandBarElement element)
        {
            throw new InvalidOperationException($"Resource '{resourceKey}' did not create an ICommandBarElement.");
        }

        if (element is FrameworkElement frameworkElement)
        {
            frameworkElement.DataContext = dataContext;
        }

        if (element is DependencyObject dependencyObject)
        {
            MenuFlyoutLanguageHelper.Apply(dependencyObject);
        }

        return element;
    }

    private CommandBarLabelPosition GetOperationLabelPosition(string label)
    {
        return string.IsNullOrWhiteSpace(label) || _preferencesService == null || !_preferencesService.IsShowActionLabelsEnabled
            ? CommandBarLabelPosition.Collapsed
            : CommandBarLabelPosition.Default;
    }

    private void UpdateOverflowButtonVisibility()
    {
        OverflowButtonVisibility = SecondaryCommands.Count > 0
            ? CommandBarOverflowButtonVisibility.Visible
            : CommandBarOverflowButtonVisibility.Auto;
    }

    public void InvalidateCommands()
    {
        RefreshCommands();
    }

    private sealed class SeparatorCommandBarItemViewModel;
}

public sealed class OperationCommandBarMenuOperationItemViewModel
{
    public OperationCommandBarMenuOperationItemViewModel(IMenuOperation operation, string label, WinoIconGlyph icon, CommandBarLabelPosition labelPosition)
    {
        Operation = operation;
        Label = label;
        Icon = icon;
        ToolTip = label;
        LabelPosition = labelPosition;
    }

    public IMenuOperation Operation { get; }
    public string Label { get; }
    public WinoIconGlyph Icon { get; }
    public string ToolTip { get; }
    public bool IsEnabled => Operation.IsEnabled;
    public CommandBarLabelPosition LabelPosition { get; }
}

public sealed class OperationCommandBarAIActionsItemViewModel
{
    public OperationCommandBarAIActionsItemViewModel(string toolTip, string glyph)
    {
        ToolTip = toolTip;
        Glyph = glyph;
    }

    public string ToolTip { get; }
    public string Glyph { get; }
}

public sealed class OperationCommandBarThemeItemViewModel
{
    public OperationCommandBarThemeItemViewModel(string toolTip, WinoIconGlyph icon)
    {
        ToolTip = toolTip;
        Icon = icon;
    }

    public string ToolTip { get; }
    public WinoIconGlyph Icon { get; }
}
