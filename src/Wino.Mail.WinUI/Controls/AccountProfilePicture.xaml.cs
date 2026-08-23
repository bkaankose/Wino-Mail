using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Helpers;

namespace Wino.Mail.WinUI.Controls;

public sealed partial class AccountProfilePicture : UserControl
{
    public MailAccount Account
    {
        get => (MailAccount)GetValue(AccountProperty);
        set => SetValue(AccountProperty, value);
    }

    public static readonly DependencyProperty AccountProperty = DependencyProperty.Register(
        nameof(Account),
        typeof(MailAccount),
        typeof(AccountProfilePicture),
        new PropertyMetadata(null, OnAccountChanged));

    public bool ShowProviderFallback
    {
        get => (bool)GetValue(ShowProviderFallbackProperty);
        set => SetValue(ShowProviderFallbackProperty, value);
    }

    public static readonly DependencyProperty ShowProviderFallbackProperty = DependencyProperty.Register(
        nameof(ShowProviderFallback),
        typeof(bool),
        typeof(AccountProfilePicture),
        new PropertyMetadata(true, OnShowProviderFallbackChanged));

    public AccountProfilePicture()
    {
        InitializeComponent();
    }

    private static void OnAccountChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
        => ((AccountProfilePicture)dependencyObject).UpdatePresentation();

    private static void OnShowProviderFallbackChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
        => ((AccountProfilePicture)dependencyObject).UpdatePresentation();

    private void UpdatePresentation()
    {
        if (Account == null)
        {
            ProfilePictureRoot.Visibility = Visibility.Collapsed;
            ProviderIcon.Visibility = Visibility.Collapsed;
            ProfileImage.Fill = null;
            ProfileImage.Visibility = Visibility.Collapsed;
            AccountColorBorder.Stroke = null;
            AccountColorBorder.Visibility = Visibility.Collapsed;
            return;
        }

        var image = ResolveProfileImage(Account);
        var hasProfileImage = image != null;
        var showProviderIcon = !hasProfileImage && ShowProviderFallback;
        var showAccountColorBorder = hasProfileImage && !string.IsNullOrWhiteSpace(Account.AccountColorHex);
        var accountBrush = string.IsNullOrWhiteSpace(Account.AccountColorHex)
            ? null
            : XamlHelpers.GetSolidColorBrushFromHex(Account.AccountColorHex);
        var providerBrush = XamlHelpers.GetSolidColorBrushFromHex(Account.AccountColorHex);

        ProfilePictureRoot.Visibility = hasProfileImage || showProviderIcon ? Visibility.Visible : Visibility.Collapsed;
        ProviderIcon.Icon = XamlHelpers.GetProviderIcon(Account);
        ProviderIcon.Foreground = providerBrush;
        ProviderIcon.Visibility = showProviderIcon ? Visibility.Visible : Visibility.Collapsed;
        ProfileImage.Fill = image == null ? null : new ImageBrush { ImageSource = image, Stretch = Stretch.UniformToFill };
        ProfileImage.Visibility = hasProfileImage ? Visibility.Visible : Visibility.Collapsed;
        AccountColorBorder.Stroke = showAccountColorBorder ? accountBrush : null;
        AccountColorBorder.Visibility = showAccountColorBorder ? Visibility.Visible : Visibility.Collapsed;
    }

    private static BitmapImage ResolveProfileImage(MailAccount account)
    {
        if (account?.ProfilePictureFileId is not { } fileId)
            return null;

        var fileService = WinoApplication.Current.Services.GetService<IAccountProfilePictureFileService>();
        var uri = fileService?.GetProfilePictureUri(fileId);
        return uri == null ? null : new BitmapImage(uri);
    }
}
