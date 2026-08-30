using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Enums;
using Wino.Views.Abstract;

namespace Wino.Views.Settings;

public sealed partial class ApplicationThemeEditorPage : ApplicationThemeEditorPageAbstract
{
    public ApplicationThemeEditorPage() => InitializeComponent();

    private void FocalPointClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string value } && Enum.TryParse<ThemeWallpaperAlignment>(value, out var alignment))
            ViewModel.SelectFocalPointCommand.Execute(alignment);
    }
}
