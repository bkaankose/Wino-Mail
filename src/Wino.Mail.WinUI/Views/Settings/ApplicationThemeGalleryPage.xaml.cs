using System;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Personalization;
using Wino.Helpers;
using Wino.Mail.Controls.Core.ContextFlyout;
using Wino.Views.Abstract;

namespace Wino.Views.Settings;

public sealed partial class ApplicationThemeGalleryPage : ApplicationThemeGalleryPageAbstract
{
    public ApplicationThemeGalleryPage() => InitializeComponent();

    private const string CustomThemeAssetUriPrefix = "ms-appdata:///local/" + Wino.Services.NewThemeService.CustomThemeFolderName + "/";

    // Custom theme thumbnails keep their file name across saves, so they are read into memory:
    // a URI source would show the cached old thumbnail and keep the file open while it is replaced.
    public static ImageSource? GetPreviewImage(string? previewImage)
    {
        if (string.IsNullOrWhiteSpace(previewImage))
            return null;

        if (!previewImage.StartsWith(CustomThemeAssetUriPrefix, StringComparison.OrdinalIgnoreCase))
            return new BitmapImage(new Uri(previewImage));

        try
        {
            var path = Wino.Services.NewThemeService.GetCustomThemeAssetPath(previewImage[CustomThemeAssetUriPrefix.Length..]);
            if (!File.Exists(path))
                return null;

            using var stream = new MemoryStream(File.ReadAllBytes(path));
            var bitmap = new BitmapImage();
            bitmap.SetSource(stream.AsRandomAccessStream());
            return bitmap;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Custom theme preview {PreviewImage} could not be loaded.", previewImage);
            return null;
        }
    }

    public static Visibility CustomVisibility(AppThemeBase theme)
        => theme?.AppThemeType == AppThemeType.Custom ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility CompatibilityVisibility(AppThemeBase theme)
        => theme?.AppThemeType == AppThemeType.Custom ? Visibility.Collapsed : Visibility.Visible;

    public static string CompatibilityLabel(AppThemeBase theme) => theme?.Compatibility switch
    {
        ThemeCompatibility.Light => Translator.ApplicationThemeGallery_Light,
        ThemeCompatibility.Dark => Translator.ApplicationThemeGallery_Dark,
        _ => Translator.ApplicationThemeGallery_Both
    };

    private void ThemeGridItemClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is AppThemeBase theme && ViewModel.ApplyThemeCommand.CanExecute(theme))
            ViewModel.ApplyThemeCommand.Execute(theme);
    }

    private void ThemeCardContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: AppThemeBase theme } target)
            return;

        if (!theme.IsCustomTheme)
        {
            args.Handled = true;
            return;
        }

        WinoContextFlyoutHelper.Show(target, args, (ContextFlyoutMenuEntry[])
        [
            new ContextFlyoutCommandEntry
            {
                Text = Translator.Buttons_Edit,
                Icon = new ContextFlyoutIcon(WinoIconGlyphs.GetGlyph(WinoIconGlyph.Edit)),
                Command = ViewModel.EditThemeCommand,
                CommandParameter = theme,
                AutomationId = "ApplicationThemeGalleryEdit"
            },
            new ContextFlyoutCommandEntry
            {
                Text = Translator.Buttons_Delete,
                Icon = new ContextFlyoutIcon(WinoIconGlyphs.GetGlyph(WinoIconGlyph.Delete)),
                Command = ViewModel.RemoveThemeCommand,
                CommandParameter = theme,
                IsDestructive = true,
                Shortcut = new ContextFlyoutShortcut("Delete", "Delete"),
                AutomationId = "ApplicationThemeGalleryDelete"
            }
        ]);
    }

    private void EditThemeClick(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: AppThemeBase theme } && ViewModel.EditThemeCommand.CanExecute(theme))
            ViewModel.EditThemeCommand.Execute(theme);
    }
}
