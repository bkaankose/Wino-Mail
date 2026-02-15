using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.ViewModels.Data;
using Wino.Helpers;

namespace Wino.Mail.WinUI.Dialogs;

public sealed partial class NewAccountDialog : ContentDialog
{
    private Dictionary<SpecialImapProvider, string> helpingLinks = new Dictionary<SpecialImapProvider, string>()
    {
        { SpecialImapProvider.iCloud, "https://support.apple.com/en-us/102654" },
        { SpecialImapProvider.Yahoo, "http://help.yahoo.com/kb/SLN15241.html" },
    };

    public static readonly DependencyProperty IsProviderSelectionVisibleProperty = DependencyProperty.Register(nameof(IsProviderSelectionVisible), typeof(bool), typeof(NewAccountDialog), new PropertyMetadata(true));
    public static readonly DependencyProperty IsSpecialImapServerPartVisibleProperty = DependencyProperty.Register(nameof(IsSpecialImapServerPartVisible), typeof(bool), typeof(NewAccountDialog), new PropertyMetadata(false));
    public static readonly DependencyProperty SelectedMailProviderProperty = DependencyProperty.Register(nameof(SelectedMailProvider), typeof(ProviderDetail), typeof(NewAccountDialog), new PropertyMetadata(null, new PropertyChangedCallback(OnSelectedProviderChanged)));
    public static readonly DependencyProperty SelectedColorProperty = DependencyProperty.Register(nameof(SelectedColor), typeof(AppColorViewModel), typeof(NewAccountDialog), new PropertyMetadata(null, new PropertyChangedCallback(OnSelectedColorChanged)));
    public static readonly DependencyProperty SelectedCalendarModeIndexProperty = DependencyProperty.Register(nameof(SelectedCalendarModeIndex), typeof(int), typeof(NewAccountDialog), new PropertyMetadata(0));


    public AppColorViewModel SelectedColor
    {
        get { return (AppColorViewModel)GetValue(SelectedColorProperty); }
        set { SetValue(SelectedColorProperty, value); }
    }

    public int SelectedCalendarModeIndex
    {
        get { return (int)GetValue(SelectedCalendarModeIndexProperty); }
        set { SetValue(SelectedCalendarModeIndexProperty, value); }
    }

    /// <summary>
    /// Gets or sets current selected mail provider in the dialog.
    /// </summary>
    public ProviderDetail SelectedMailProvider
    {
        get { return (ProviderDetail)GetValue(SelectedMailProviderProperty); }
        set { SetValue(SelectedMailProviderProperty, value); }
    }


    public bool IsProviderSelectionVisible
    {
        get { return (bool)GetValue(IsProviderSelectionVisibleProperty); }
        set { SetValue(IsProviderSelectionVisibleProperty, value); }
    }

    public bool IsSpecialImapServerPartVisible
    {
        get { return (bool)GetValue(IsSpecialImapServerPartVisibleProperty); }
        set { SetValue(IsSpecialImapServerPartVisibleProperty, value); }
    }

    // List of available mail providers for now.

    public List<IProviderDetail> Providers { get; set; }

    public List<AppColorViewModel> AvailableColors { get; set; }
    public List<string> CalendarModeOptions { get; } =
    [
        Translator.ImapCalDavSettingsPage_CalendarModeCalDav,
        Translator.ImapCalDavSettingsPage_CalendarModeLocalOnly,
        Translator.ImapCalDavSettingsPage_CalendarModeDisabled
    ];


    public AccountCreationDialogResult Result = null;

    public NewAccountDialog()
    {
        InitializeComponent();

        var themeService = WinoApplication.Current.NewThemeService.GetAvailableAccountColors();
        AvailableColors = themeService.Select(a => new AppColorViewModel(a)).ToList();

        UpdateSelectedColor();
    }

    private static void OnSelectedProviderChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
    {
        if (obj is NewAccountDialog dialog)
            dialog.Validate();
    }

    private static void OnSelectedColorChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
    {
        if (obj is NewAccountDialog dialog)
            dialog.UpdateSelectedColor();
    }

    private void UpdateSelectedColor()
    {
        PickColorTextblock.Visibility = SelectedColor == null ? Visibility.Visible : Visibility.Collapsed;
        SelectedColorEllipse.Fill = SelectedColor == null ? null : XamlHelpers.GetSolidColorBrushFromHex(SelectedColor.Hex);
    }


    private void CancelClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        Hide();
    }

    private void CreateClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (IsSpecialImapServerPartVisible)
        {
            // Special imap detail input.
            var calendarSupportMode = SelectedCalendarModeIndex switch
            {
                1 => ImapCalendarSupportMode.LocalOnly,
                2 => ImapCalendarSupportMode.Disabled,
                _ => ImapCalendarSupportMode.CalDav
            };

            var details = new SpecialImapProviderDetails(
                SpecialImapAddress.Text.Trim(),
                AppSpecificPassword.Password.Trim(),
                DisplayNameTextBox.Text.Trim(),
                SelectedMailProvider.SpecialImapProvider,
                calendarSupportMode);
            Result = new AccountCreationDialogResult(SelectedMailProvider.Type, AccountNameTextbox.Text.Trim(), details, SelectedColor?.Hex ?? string.Empty);
            Hide();

            return;
        }

        Validate();

        if (IsSecondaryButtonEnabled)
        {
            if (SelectedMailProvider.SpecialImapProvider != SpecialImapProvider.None)
            {
                // This step requires app-sepcific password login for some providers.
                args.Cancel = true;

                IsProviderSelectionVisible = false;
                IsSpecialImapServerPartVisible = true;

                Validate();
            }
            else
            {
                Result = new AccountCreationDialogResult(SelectedMailProvider.Type, AccountNameTextbox.Text.Trim(), null, SelectedColor?.Hex ?? string.Empty);
                Hide();
            }
        }
    }

    private void InputChanged(object sender, TextChangedEventArgs e) => Validate();
    private void SenderNameChanged(object sender, TextChangedEventArgs e) => Validate();

    private void Validate()
    {
        ValidateCreateButton();
        ValidateNames();
    }

    // Returns whether we can create account or not.
    private void ValidateCreateButton()
    {
        bool shouldEnable = SelectedMailProvider != null
            && SelectedMailProvider.IsSupported
            && !string.IsNullOrEmpty(AccountNameTextbox.Text)
            && (IsSpecialImapServerPartVisible ? (!string.IsNullOrEmpty(AppSpecificPassword.Password)
            && !string.IsNullOrEmpty(DisplayNameTextBox.Text)
            && EmailValidation.EmailValidator.Validate(SpecialImapAddress.Text)) : true);

        IsPrimaryButtonEnabled = shouldEnable;
    }

    private void ValidateNames()
    {
        AccountNameTextbox.IsEnabled = SelectedMailProvider != null;
    }

    private void DialogOpened(ContentDialog sender, ContentDialogOpenedEventArgs args) => Validate();

    private void BackClicked(object sender, RoutedEventArgs e)
    {
        IsSpecialImapServerPartVisible = false;
        IsProviderSelectionVisible = true;

        Validate();
    }

    private void ImapPasswordChanged(object sender, RoutedEventArgs e) => Validate();

    private async void AppSpecificHelpButtonClicked(object sender, RoutedEventArgs e)
    {
        var helpUrl = helpingLinks[SelectedMailProvider.SpecialImapProvider];

        await Launcher.LaunchUriAsync(new Uri(helpUrl));
    }
}
