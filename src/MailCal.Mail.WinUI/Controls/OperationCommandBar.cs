using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.WinUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Data;
using MailCal.Core.Domain;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Domain.Models.Folders;
using MailCal.Core.Domain.Models.Menus;
using MailCal.Helpers;
using MailCal.Messaging.Client.Shell;
using WinRT;

namespace MailCal.Mail.WinUI.Controls;

public sealed partial class OperationCommandBar : CommandBar, IRecipient<LanguageChanged>
{
    private const string MailOperationTemplateKey = "OperationCommandBarMailOperationTemplate";
    private const string FolderOperationTemplateKey = "OperationCommandBarFolderOperationTemplate";
    private const string PopOutTemplateKey = "OperationCommandBarThemeToggleTemplate";
    private const string ThemeToggleTemplateKey = "OperationCommandBarThemeToggleTemplate";
    private const string SeparatorTemplateKey = "OperationCommandBarSeparatorTemplate";

    private readonly IPreferencesService? _preferencesService;
    private bool _isCommandRefreshQueued;
    private string? _renderedCommandSignature;

    [GeneratedDependencyProperty]
    public partial IReadOnlyList<IMenuOperation>? MenuItems { get; set; }

    [GeneratedDependencyProperty]
    public partial ICommand? ItemInvokedCommand { get; set; }

    [GeneratedDependencyProperty]
    public partial bool IsEditorThemeDark { get; set; }

    [GeneratedDependencyProperty]
    public partial bool IsEditorThemeToggleVisible { get; set; }

    [GeneratedDependencyProperty]
    public partial bool IsPopOutButtonVisible { get; set; }

    [GeneratedDependencyProperty]
    public partial bool IsReaderViewToggleVisible { get; set; }

    [GeneratedDependencyProperty]
    public partial bool IsReaderViewEnabled { get; set; }

    public event EventHandler? PopOutClicked;

    public OperationCommandBar()
    {
        _preferencesService = App.Current.Services.GetService<IPreferencesService>();

        DefaultLabelPosition = CommandBarDefaultLabelPosition.Right;
        IsDynamicOverflowEnabled = true;
        OverflowButtonVisibility = CommandBarOverflowButtonVisibility.Auto;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DynamicOverflowItemsChanging += OperationCommandBar_DynamicOverflowItemsChanging;
    }

    partial void OnMenuItemsPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        QueueCommandRefresh();
    }

    partial void OnItemInvokedCommandChanged(ICommand? newValue)
    {
        InvalidateCommands();
    }

    partial void OnIsEditorThemeDarkChanged(bool newValue)
    {
        QueueCommandRefresh();
    }

    partial void OnIsEditorThemeToggleVisibleChanged(bool newValue)
    {
        QueueCommandRefresh();
    }

    partial void OnIsPopOutButtonVisibleChanged(bool newValue)
    {
        QueueCommandRefresh();
    }

    partial void OnIsReaderViewToggleVisibleChanged(bool newValue)
    {
        QueueCommandRefresh();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_preferencesService != null)
        {
            _preferencesService.PreferenceChanged -= PreferencesService_PreferenceChanged;
            _preferencesService.PreferenceChanged += PreferencesService_PreferenceChanged;
        }

        WeakReferenceMessenger.Default.Unregister<LanguageChanged>(this);
        WeakReferenceMessenger.Default.Register<LanguageChanged>(this);
        InvalidateCommands();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_preferencesService != null)
        {
            _preferencesService.PreferenceChanged -= PreferencesService_PreferenceChanged;
        }

        WeakReferenceMessenger.Default.Unregister<LanguageChanged>(this);
    }

    private void PreferencesService_PreferenceChanged(object? sender, string propertyName)
    {
        if (propertyName == nameof(IPreferencesService.IsShowActionLabelsEnabled))
        {
            DispatcherQueue.TryEnqueue(InvalidateCommands);
        }
    }

    public void Receive(LanguageChanged message)
    {
        DispatcherQueue.TryEnqueue(InvalidateCommands);
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

    private void QueueCommandRefresh()
    {
        if (!IsLoaded || _isCommandRefreshQueued)
        {
            return;
        }

        _isCommandRefreshQueued = true;
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _isCommandRefreshQueued = false;
            RefreshCommands();
        }))
        {
            _isCommandRefreshQueued = false;
            RefreshCommands();
        }
    }

    private void RefreshCommands()
    {
        var commandSignature = CreateCommandSignature();
        if (string.Equals(_renderedCommandSignature, commandSignature, StringComparison.Ordinal))
        {
            return;
        }

        ClearGeneratedCommands();

        if (IsPopOutButtonVisible)
        {
            PrimaryCommands.Add(CreatePopOutButton());
        }

        if (IsEditorThemeToggleVisible)
        {
            PrimaryCommands.Add(CreateThemeToggleButton());
        }

        if (IsReaderViewToggleVisible)
        {
            SecondaryCommands.Add(CreateReaderViewToggleButton());
        }

        if (MenuItems == null)
        {
            UpdateOverflowButtonVisibility();
            _renderedCommandSignature = commandSignature;
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
        _renderedCommandSignature = commandSignature;
    }

    private string CreateCommandSignature()
    {
        var signature = new StringBuilder();
        signature
            .Append(IsPopOutButtonVisible).Append('|')
            .Append(IsEditorThemeToggleVisible).Append('|')
            .Append(IsReaderViewToggleVisible).Append('|')
            .Append(IsEditorThemeDark).Append('|')
            .Append(_preferencesService?.IsShowActionLabelsEnabled == true).Append('|');

        foreach (var item in MenuItems ?? [])
        {
            var identifier = item?.Identifier ?? string.Empty;
            signature
                .Append(item?.GetType().FullName).Append(':')
                .Append(identifier.Length).Append(':')
                .Append(identifier).Append(':')
                .Append(item?.IsEnabled).Append(':')
                .Append(item?.IsSecondaryMenuPreferred).Append('|');
        }

        return signature.ToString();
    }

    private void ClearGeneratedCommands()
    {
        DetachCommandHandlers(PrimaryCommands);
        DetachCommandHandlers(SecondaryCommands);

        PrimaryCommands.Clear();
        SecondaryCommands.Clear();
    }

    [DynamicWindowsRuntimeCast(typeof(AppBarButton))]
    [DynamicWindowsRuntimeCast(typeof(AppBarToggleButton))]
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

    [DynamicWindowsRuntimeCast(typeof(AppBarButton))]
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
                    XamlHelpers.GetMailCalIconGlyph(mailOperationItem.Operation),
                    GetOperationLabelPosition(XamlHelpers.GetOperationString(mailOperationItem.Operation))))
                as AppBarButton;

            if (button == null)
            {
                return null;
            }

            button.Tag = mailOperationItem;

            if (mailOperationItem.Operation == MailOperation.SaveAs)
            {
                button.Flyout = CreateSaveAsFlyout();
            }
            else
            {
                button.Click += OperationButton_Click;
            }

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

    [DynamicWindowsRuntimeCast(typeof(AppBarButton))]
    private AppBarButton CreateThemeToggleButton()
    {
        var label = IsEditorThemeDark ? Translator.Composer_LightTheme : Translator.Composer_DarkTheme;
        var icon = IsEditorThemeDark ? MailCalIconGlyph.LightEditor : MailCalIconGlyph.DarkEditor;

        var button = (AppBarButton)LoadCommandBarElementTemplate(
            ThemeToggleTemplateKey,
            new OperationCommandBarThemeItemViewModel(label, icon));

        button.Click += ThemeButton_Click;
        return button;
    }

    [DynamicWindowsRuntimeCast(typeof(AppBarButton))]
    private AppBarButton CreatePopOutButton()
    {
        var button = (AppBarButton)LoadCommandBarElementTemplate(
            PopOutTemplateKey,
            new OperationCommandBarThemeItemViewModel(Translator.Buttons_PopOut, MailCalIconGlyph.OpenInNewWindow));

        button.Click += PopOutButton_Click;
        return button;
    }

    private AppBarToggleButton CreateReaderViewToggleButton()
    {
        var button = new AppBarToggleButton
        {
            Label = Translator.Reader_ReaderView,
            Icon = new MailCalFontIcon { Icon = MailCalIconGlyph.Document },
        };
        AutomationProperties.SetName(button, Translator.Reader_ReaderView);
        button.SetBinding(AppBarToggleButton.IsCheckedProperty, new Binding
        {
            Mode = BindingMode.TwoWay,
            Path = new PropertyPath(nameof(IsReaderViewEnabled)),
            Source = this
        });
        return button;
    }

    [DynamicWindowsRuntimeCast(typeof(AppBarButton))]
    private void OperationButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is AppBarButton button && button.Tag is IMenuOperation operation)
        {
            ItemInvokedCommand?.Execute(operation);
        }
    }

    private MenuFlyout CreateSaveAsFlyout()
    {
        var flyout = new MenuFlyout();
        flyout.Items.Add(CreateSaveAsFlyoutItem(MailOperation.SaveAsPdf, Translator.Buttons_PDF, MailCalIconGlyph.Save));
        flyout.Items.Add(CreateSaveAsFlyoutItem(MailOperation.SaveAsEml, Translator.Buttons_EML, MailCalIconGlyph.ViewMessageSource));

        MenuFlyoutLanguageHelper.Apply(flyout);

        return flyout;
    }

    private MenuFlyoutItem CreateSaveAsFlyoutItem(MailOperation operation, string text, MailCalIconGlyph icon)
    {
        var item = new MenuFlyoutItem
        {
            Text = text,
            Icon = new MailCalFontIcon
            {
                Icon = icon
            },
            Command = ItemInvokedCommand,
            CommandParameter = MailOperationMenuItem.Create(operation)
        };

        return item;
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

    [DynamicWindowsRuntimeCast(typeof(DataTemplate))]
    [DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
    [DynamicWindowsRuntimeCast(typeof(DependencyObject))]
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
        _renderedCommandSignature = null;
        QueueCommandRefresh();
    }

    private sealed class SeparatorCommandBarItemViewModel;
}

public sealed class OperationCommandBarMenuOperationItemViewModel
{
    public OperationCommandBarMenuOperationItemViewModel(IMenuOperation operation, string label, MailCalIconGlyph icon, CommandBarLabelPosition labelPosition)
    {
        Operation = operation;
        Label = label;
        Icon = icon;
        ToolTip = label;
        LabelPosition = labelPosition;
    }

    public IMenuOperation Operation { get; }
    public string Label { get; }
    public MailCalIconGlyph Icon { get; }
    public string ToolTip { get; }
    public bool IsEnabled => Operation.IsEnabled;
    public CommandBarLabelPosition LabelPosition { get; }
}

public sealed class OperationCommandBarThemeItemViewModel
{
    public OperationCommandBarThemeItemViewModel(string toolTip, MailCalIconGlyph icon)
    {
        ToolTip = toolTip;
        Icon = icon;
    }

    public string ToolTip { get; }
    public MailCalIconGlyph Icon { get; }
}
