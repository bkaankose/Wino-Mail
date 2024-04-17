using System;
using CommunityToolkit.WinUI.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Wino.Core.Domain.Interfaces;
using Wino.Services;

namespace Wino.Dialogs
{
    public sealed partial class CustomThemeBuilderDialog : ContentDialog
    {
        public byte[] WallpaperData { get; private set; }
        public string AccentColor { get; private set; }

        private IThemeService _themeService;

        public CustomThemeBuilderDialog()
        {
            InitializeComponent();

            _themeService = App.Current.Services.GetService<IThemeService>();
        }

        private async void ApplyClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            if (Array.Empty<byte>() == WallpaperData)
                return;

            var deferal = args.GetDeferral();

            try
            {
                await _themeService.CreateNewCustomThemeAsync(ThemeNameBox.Text, AccentColor, WallpaperData);
            }
            catch (Exception exception)
            {
                ErrorTextBlock.Text = exception.Message;
            }
            finally
            {
                deferal.Complete();
            }
        }

        private async void BrowseWallpaperClicked(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            var dialogService = App.Current.Services.GetService<IDialogService>();

            var pickedFileData = await dialogService.PickWindowsFileContentAsync(".jpg", ".png");

            if (pickedFileData == Array.Empty<byte>()) return;

            IsPrimaryButtonEnabled = true;

            WallpaperData = pickedFileData;
        }

        private void PickerColorChanged(Microsoft.UI.Xaml.Controls.ColorPicker sender, Microsoft.UI.Xaml.Controls.ColorChangedEventArgs args)
        {
            PreviewAccentColorGrid.Background = new SolidColorBrush(args.NewColor);
            AccentColor = args.NewColor.ToHex();
        }
    }
}
