using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Personalization;
using Wino.Views.Abstract;

namespace Wino.Views.Settings;

public sealed partial class ApplicationThemeEditorPage : ApplicationThemeEditorPageAbstract
{
    public ApplicationThemeEditorPage() => InitializeComponent();

    public static ImageSource? GetWallpaperPreviewSource(string? path)
    {
        var uri = ThemeWallpaperPreviewPath.GetAbsoluteUri(path);
        return uri == null ? null : new BitmapImage(uri);
    }

    private void FocalPointClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string value } && Enum.TryParse<ThemeWallpaperAlignment>(value, out var alignment))
            ViewModel.SelectFocalPointCommand.Execute(alignment);
    }
}
