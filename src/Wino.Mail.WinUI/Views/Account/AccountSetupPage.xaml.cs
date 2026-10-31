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
using Wino.Core.Domain.Enums;
using Wino.Helpers;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.WinUI.Views.Abstract;
using WinRT;

namespace Wino.Views;

/// <summary>
/// Step-by-step account setup in one panel: the identity strip on top, the step in the body and Back with the primary
/// button in the footer. The step host shows one template per <see cref="AccountSetupStep"/>; the page keeps the body
/// header (logo, app name, question), the strip and the focus after each step.
/// </summary>
public sealed partial class AccountSetupPage : AccountSetupPageAbstract
{
    private const double HeroLogoSize = 96;
    private const double AppLogoSize = 56;

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

    #region Body header

    /// <summary>
    /// The address step shows the Wino Mail lockup at 96 epx, a mode step its app's logo at 56 epx above the app name;
    /// the question under them comes from the view model. Other steps have no logo.
    /// </summary>
    private void ShowHeader(AccountSetupStep step)
    {
        var logo = step switch
        {
            AccountSetupStep.Address or AccountSetupStep.Mail => "Mail.svg",
            AccountSetupStep.Calendar => "Calendar.svg",
            AccountSetupStep.Contacts => "People.svg",
            AccountSetupStep.Tasks => "ToDo.svg",
            _ => null
        };

        var isHero = step == AccountSetupStep.Address;
        var hasQuestion = step is AccountSetupStep.Mail or AccountSetupStep.Calendar or AccountSetupStep.Contacts or AccountSetupStep.Tasks
            or AccountSetupStep.SignInMethod or AccountSetupStep.Progress;

        if (logo == null)
        {
            StepLogo.Source = null;
            StepLogo.Visibility = Visibility.Collapsed;
        }
        else
        {
            var size = isHero ? HeroLogoSize : AppLogoSize;
            StepLogo.Width = size;
            StepLogo.Height = size;
            StepLogo.Source = new SvgImageSource(new Uri($"ms-appx:///Assets/AppEntries/{logo}"));
            StepLogo.Visibility = Visibility.Visible;
        }

        HeroTitle.Visibility = isHero ? Visibility.Visible : Visibility.Collapsed;
        StepAppNameText.Visibility = logo != null && !isHero ? Visibility.Visible : Visibility.Collapsed;
        StepHeader.Visibility = logo != null || hasQuestion ? Visibility.Visible : Visibility.Collapsed;
    }

    #endregion

    #region Identity strip

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
        var hasProvider = false;

        foreach (var item in ViewModel.KnownItems)
        {
            if (item.Kind == AccountSetupKnownItemKind.Identity)
            {
                identity = item.Text;
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

        KnownIdentityText.Text = identity;
        KnownProviderIcon.Icon = ViewModel.IsLocalOnlyMode
            ? WinoIconGlyph.Laptop
            : hasProvider
                ? XamlHelpers.GetProviderIcon(ViewModel.RouteProviderType, ViewModel.RouteSpecialImapProvider)
                : WinoIconGlyph.Mail;

        return chips;
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
