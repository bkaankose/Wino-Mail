using System;
using System.Collections.Generic;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Helpers;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.WinUI.Views.Abstract;
using WinRT;

namespace Wino.Views;

/// <summary>
/// Account setup as one column: the step's title, the account pill, the step, then Back and the primary button.
/// The step host shows one template per <see cref="AccountSetupStep"/>; the page keeps the title, the pill and the
/// focus after each step. The welcome window centres the column; Settings starts it at the top left like its other
/// pages.
/// </summary>
public sealed partial class AccountSetupPage : AccountSetupPageAbstract
{
    private const double StepWidth = 440;
    private const double ReviewWidth = 560;

    private readonly List<FrameworkElement> _focusCandidates = [];

    private bool _isFocusQueued;
    private AccountSetupStep? _shownStep;

    public AccountSetupPage()
    {
        InitializeComponent();

        ViewModel.IsSecureSignInSupportedByHost = true;

        Loaded += SetupPageLoaded;
        Unloaded += SetupPageUnloaded;
    }

    public static ImageSource GetAppIcon(AccountSetupAppKind kind)
    {
        var name = kind switch
        {
            AccountSetupAppKind.Calendar => "Calendar",
            AccountSetupAppKind.Contacts => "People",
            AccountSetupAppKind.Tasks => "ToDo",
            _ => "Mail"
        };

        return new SvgImageSource(new Uri($"ms-appx:///Assets/AppEntries/{name}.svg"));
    }

    public static HorizontalAlignment GetHostAlignment(bool isSettingsHost)
        => isSettingsHost ? HorizontalAlignment.Left : HorizontalAlignment.Center;

    /// <summary>Room on the email field's leading edge for the recognised provider's mark.</summary>
    public static Thickness GetEmailPadding(bool hasMatchedProvider) => new(hasMatchedProvider ? 34 : 10, 5, 6, 6);

    public static string GetUseAppName(string title) => string.Format(Translator.AccountSetup_Review_UseApp, title);

    public static string GetSourceName(AccountSetupAppKind kind, string title) => kind == AccountSetupAppKind.Mail
        ? Translator.AccountSetup_Review_MailRange
        : string.Format(Translator.AccountSetup_Review_SourceOf, title);

    /// <summary>The provider's mark on the browser sign-in button when Wino has one, otherwise a globe.</summary>
    public static WinoIconGlyph GetBrowserSignInMark(SpecialImapProvider specialImapProvider)
    {
        var glyph = XamlHelpers.GetProviderIcon(MailProviderType.IMAP4, specialImapProvider);
        return glyph is WinoIconGlyph.IMAP or WinoIconGlyph.None ? WinoIconGlyph.Globe : glyph;
    }

    private void SetupPageLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= ViewModelPropertyChanged;
        ViewModel.PropertyChanged += ViewModelPropertyChanged;

        // The wizard centres the column in its window; Settings pages start at the top left. A left-aligned column
        // would size to its content and change width between steps, so in Settings the first grid column carries the
        // width and a second one takes the rest.
        var isSettings = ViewModel.IsSettingsHost;
        TopSpacerRow.Height = isSettings ? new GridLength(0) : new GridLength(1, GridUnitType.Star);

        if (isSettings && SetupRoot.ColumnDefinitions.Count == 0)
        {
            SetupRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1000, GridUnitType.Star), MaxWidth = StepWidth });
            SetupRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        var alignment = GetHostAlignment(isSettings);
        var textAlignment = isSettings ? TextAlignment.Left : TextAlignment.Center;
        StepTitleText.HorizontalAlignment = alignment;
        StepTitleText.TextAlignment = textAlignment;
        StepSubtitleText.HorizontalAlignment = alignment;
        StepSubtitleText.TextAlignment = textAlignment;
        KnownRow.HorizontalAlignment = alignment;
        SetupLinks.HorizontalAlignment = alignment;

        RenderKnownItems(isAnnounced: false);
        ShowStep(ViewModel.Step);
    }

    private void SetupPageUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= ViewModelPropertyChanged;

        _shownStep = null;
        StepHost.Content = null;
        ClearStepState();
    }

    private void ViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            var propertyName = e.PropertyName;
            DispatcherQueue.TryEnqueue(() => OnViewModelPropertyChanged(propertyName));
            return;
        }

        OnViewModelPropertyChanged(e.PropertyName);
    }

    private void OnViewModelPropertyChanged(string? propertyName)
    {
        switch (propertyName)
        {
            case nameof(ViewModel.Step):
                ShowStep(ViewModel.Step);
                break;
            case nameof(ViewModel.KnownText):
                RenderKnownItems(isAnnounced: true);
                break;
            case nameof(ViewModel.SelectedColor):
                RenderStripMarkForeground();
                break;
        }
    }

    #region Steps

    private void ShowStep(AccountSetupStep step)
    {
        if (_shownStep == step && StepHost.Content != null)
            return;

        _shownStep = step;
        ClearStepState();
        ShowHeader(step);
        ApplyStepWidth(step);

        StepHost.Content = new AccountSetupStepContent(ViewModel, step);
    }

    private void ClearStepState()
    {
        _focusCandidates.Clear();
        _isFocusQueued = false;
    }

    private void FocusTargetLoaded(object sender, RoutedEventArgs e) => AddFocusCandidate(sender);

    [DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
    private void AddFocusCandidate(object sender)
    {
        if (sender is not FrameworkElement element)
            return;

        _focusCandidates.Add(element);

        if (_isFocusQueued)
            return;

        _isFocusQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, FocusFirstCandidate);
    }

    /// <summary>After a step appears, focus goes to its first visible control: the selected item, the field, or the button.</summary>
    [DynamicWindowsRuntimeCast(typeof(ItemsView))]
    [DynamicWindowsRuntimeCast(typeof(Control))]
    private void FocusFirstCandidate()
    {
        _isFocusQueued = false;

        foreach (var candidate in _focusCandidates)
        {
            if (!IsShown(candidate))
                continue;

            if (candidate is ItemsView itemsView)
            {
                var index = ViewModel.SelectedChoiceIndex;
                var repeater = FindItemsRepeater(itemsView);
                if (index >= 0 && repeater?.TryGetElement(index) is Control container && container.Focus(FocusState.Programmatic))
                    return;

                continue;
            }

            if (candidate is Control control && control.IsEnabled && control.Focus(FocusState.Programmatic))
                return;
        }
    }

    [DynamicWindowsRuntimeCast(typeof(UIElement))]
    private bool IsShown(FrameworkElement element)
    {
        DependencyObject? current = element;

        while (current != null && !ReferenceEquals(current, StepHost))
        {
            if (current is UIElement uiElement && uiElement.Visibility != Visibility.Visible)
                return false;

            current = VisualTreeHelper.GetParent(current);
        }

        return current != null;
    }

    [DynamicWindowsRuntimeCast(typeof(ItemsRepeater))]
    private static ItemsRepeater? FindItemsRepeater(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ItemsRepeater repeater)
                return repeater;

            var nested = FindItemsRepeater(child);
            if (nested != null)
                return nested;
        }

        return null;
    }

    private void ExecutePrimary()
    {
        if (ViewModel.IsPrimaryVisible && ViewModel.PrimaryCommand.CanExecute(null))
            ViewModel.PrimaryCommand.Execute(null);
    }

    private void StepTextBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;

        e.Handled = true;
        ExecutePrimary();
    }

    #endregion

    #region Choices

    [DynamicWindowsRuntimeCast(typeof(ItemsView))]
    private void ChoicesLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ItemsView itemsView)
            return;

        var index = ViewModel.SelectedChoiceIndex;
        if (index >= 0)
            itemsView.Select(index);

        AddFocusCandidate(itemsView);
    }

    private void ChoicesSelectionChanged(ItemsView sender, ItemsViewSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is AccountSetupChoice choice && ViewModel.StepChoices.Contains(choice))
        {
            ViewModel.SelectedChoice = choice;
            return;
        }

        // Single selection can be cleared with Ctrl+Space; a step always keeps one item selected.
        var index = ViewModel.SelectedChoiceIndex;
        if (sender.SelectedItem == null && index >= 0 && sender.IsLoaded)
            sender.Select(index);
    }

    private void ChoicesPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;

        e.Handled = true;
        ExecutePrimary();
    }

    #endregion

    #region Title

    /// <summary>The address step shows the Wino Mail lockup, centred in the column in both hosts; no other step has a logo.</summary>
    private void ShowHeader(AccountSetupStep step)
    {
        if (step == AccountSetupStep.Address)
        {
            StepLogo.Source = new SvgImageSource(new Uri("ms-appx:///Assets/AppEntries/Mail.svg"));
            StepLogo.Visibility = Visibility.Visible;
            return;
        }

        StepLogo.Source = null;
        StepLogo.Visibility = Visibility.Collapsed;
    }

    /// <summary>The review step's cards need more room than a single field.</summary>
    private void ApplyStepWidth(AccountSetupStep step)
    {
        var width = step == AccountSetupStep.Review ? ReviewWidth : StepWidth;
        SetupPanel.MaxWidth = width;

        if (SetupRoot.ColumnDefinitions.Count > 0)
            SetupRoot.ColumnDefinitions[0].MaxWidth = width;
    }

    #endregion

    #region Account pill

    private void RenderKnownItems(bool isAnnounced)
    {
        KnownItems.ItemsSource = BuildKnownChips();
        RenderStripMarkForeground();

        if (isAnnounced)
            FrameworkElementAutomationPeer.FromElement(KnownCard)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    /// <summary>
    /// The address (or a local account's name) is the strip's first line; the provider and capabilities follow under it.
    /// The circle shows the generic mail glyph until discovery names the provider.
    /// </summary>
    private List<AccountSetupKnownChip> BuildKnownChips()
    {
        var chips = new List<AccountSetupKnownChip>();
        var identity = string.Empty;
        var reason = string.Empty;
        var hasProvider = false;

        foreach (var item in ViewModel.KnownItems)
        {
            if (item.Kind == AccountSetupKnownItemKind.Identity)
            {
                identity = item.Text;
                continue;
            }

            if (item.Kind == AccountSetupKnownItemKind.Reason)
            {
                reason = item.Text;
                continue;
            }

            hasProvider |= item.Kind == AccountSetupKnownItemKind.Provider;

            chips.Add(new AccountSetupKnownChip(
                item.Text,
                item.Kind == AccountSetupKnownItemKind.SignIn ? WinoIconGlyph.Checkmark : WinoIconGlyph.None,
                isStrong: false,
                isSuccess: item.Kind == AccountSetupKnownItemKind.SignIn,
                hasSeparator: chips.Count > 0,
                item.HelpText));
        }

        // Why Wino chose the provider ("Found a mail server at…", "Chosen by you") is the pill's tooltip.
        KnownIdentityText.Text = identity;
        ToolTipService.SetToolTip(KnownCard, reason.Length > 0 ? reason : null);
        var providerIcon = hasProvider
            ? XamlHelpers.GetProviderIcon(ViewModel.RouteProviderType, ViewModel.RouteSpecialImapProvider)
            : WinoIconGlyph.Mail;

        // A provider without a mark of its own, such as POP3, shows the generic mail glyph.
        KnownProviderIcon.Icon = ViewModel.IsLocalOnlyMode
            ? WinoIconGlyph.Laptop
            : providerIcon == WinoIconGlyph.None ? WinoIconGlyph.Mail : providerIcon;

        return chips;
    }

    /// <summary>The strip link owns the provider picker; the failure and not-found InfoBars open the same one at their button.</summary>
    [DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
    private void ChangeProviderClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement placementTarget)
            ProviderFlyout.ShowAt(placementTarget);
    }

    private void ChangeProviderMenuClick(object sender, RoutedEventArgs e) => ProviderFlyout.ShowAt(ChangeButton);

    private void ProviderChoiceClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not AccountSetupProviderChoice choice)
            return;

        ProviderFlyout.Hide();
        ViewModel.ChangeProviderCommand.Execute(choice);
    }

    /// <summary>On the chosen account colour the glyph takes black or white, whichever reads; otherwise it inherits the theme.</summary>
    private void RenderStripMarkForeground()
    {
        var hex = ViewModel.SelectedColor?.Hex;

        if (string.IsNullOrWhiteSpace(hex))
        {
            KnownProviderIcon.ClearValue(IconElement.ForegroundProperty);
            return;
        }

        try
        {
            KnownProviderIcon.Foreground = XamlHelpers.GetReadableTextColor(hex);
        }
        catch (ArgumentException)
        {
            KnownProviderIcon.ClearValue(IconElement.ForegroundProperty);
        }
    }

    #endregion
}
