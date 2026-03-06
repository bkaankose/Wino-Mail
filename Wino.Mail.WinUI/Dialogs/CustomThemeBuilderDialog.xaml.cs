using System;
using CommunityToolkit.WinUI.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.WinUI;

namespace Wino.Dialogs;

public sealed partial class CustomThemeBuilderDialog : ContentDialog
{
    public byte[] WallpaperData { get; private set; } = Array.Empty<byte>();
    public string AccentColor { get; private set; } = string.Empty;

    private readonly INewThemeService _themeService;

    public CustomThemeBuilderDialog()
    {
        InitializeComponent();

        _themeService = WinoApplication.Current.Services.GetRequiredService<INewThemeService>();
    }

    private async void ApplyClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (WallpaperData.Length == 0)
            return;

        var deferal = args.GetDeferral();

        try
        {
            await _themeService.CreateNewCustomThemeAsync(ThemeNameBox.Text, AccentColor, WallpaperData);
        }
        catch (Exception exception)
        {
            ErrorInfoBar.Message = exception.Message;
            ErrorInfoBar.IsOpen = true;
        }
        finally
        {
            deferal.Complete();
        }
    }

    private async void BrowseWallpaperClicked(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var dialogService = WinoApplication.Current.Services.GetRequiredService<IMailDialogService>();

        var pickedFileData = await dialogService.PickWindowsFileContentAsync(".jpg", ".png");

        if (pickedFileData.Length == 0) return;

        IsPrimaryButtonEnabled = true;

        WallpaperData = pickedFileData;
    }

    private void PickerColorChanged(Microsoft.UI.Xaml.Controls.ColorPicker sender, Microsoft.UI.Xaml.Controls.ColorChangedEventArgs args)
    {
        PreviewAccentColorGrid.Background = new SolidColorBrush(args.NewColor);
        AccentColor = args.NewColor.ToHex();
    }
}
