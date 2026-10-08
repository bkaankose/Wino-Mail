using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.UI;
using Wino.Controls;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Personalization;
using Wino.Core.ViewModels.Data;
using Wino.Views.Abstract;

namespace Wino.Views.Settings;

public sealed partial class ApplicationThemeEditorPage : ApplicationThemeEditorPageAbstract
{
    private const string DefaultAccentColorHex = "#FF4CC2FF";

    // Segmented clears and re-applies its own selection while it loads. Its selection is
    // therefore pushed from the view model and only honoured as user input once loaded.
    private bool _isPaletteModeSegmentedReady;
    private bool _isWallpaperFitSegmentedReady;
    private bool _isSynchronizingSegments;

    // The colour picker only writes while its own flyout is open, so nothing it reports
    // while loading is mistaken for an edit.
    private ColorPicker? _openColorPicker;
    private bool _isSynchronizingColor;

    /// <summary>
    /// The decoded wallpaper, shared by the focal point picker and every surface preview.
    /// </summary>
    [GeneratedDependencyProperty]
    public partial ImageSource? WallpaperImageSource { get; set; }

    /// <summary>
    /// The accent the previews draw with: the Windows accent while the theme follows the
    /// system, otherwise the accent chosen for this theme.
    /// </summary>
    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string PreviewAccentColorHex { get; set; }

    public ApplicationThemeEditorPage() => InitializeComponent();

    public static string GetCustomizedCountText(int count)
        => string.Format(Translator.ApplicationThemeEditor_CustomizedCountFormat, count);

    public static Visibility GetContrastPassVisibility(bool hasReport, bool isSufficient)
        => hasReport && isSufficient ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility GetContrastWarningVisibility(bool hasReport, bool isSufficient)
        => hasReport && !isSufficient ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// The marker drawn on the wallpaper for one focal point cell. The selected cell is solid,
    /// the others stay faint so the image underneath is still readable.
    /// </summary>
    public static Brush GetFocalMarkerBrush(ThemeWallpaperAlignment current, string candidate)
    {
        var isSelected = string.Equals(current.ToString(), candidate, StringComparison.Ordinal);

        return new SolidColorBrush(isSelected
            ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF));
    }

    // Template buttons reach the page commands from code-behind: a reflection {Binding} to the
    // page view model has no metadata in Native AOT builds.
    private void ColorOptionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: ThemePaletteColorOptionViewModel option })
            ViewModel.ToggleOptionCommand.Execute(option);
    }

    private void ResetColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: ThemePaletteColorOptionViewModel option })
            ViewModel.ResetColorCommand.Execute(option);
    }

    private void BasePresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: ThemeBasePreset preset })
            ViewModel.ApplyBasePresetCommand.Execute(preset);
    }

    private void EditorLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged += EditorViewModelPropertyChanged;

        // The captured element theme can be Default, so tell the view model what the
        // application actually renders. Otherwise a dark shell opens the Light palette.
        ViewModel.ResolveSystemPaletteMode(ActualTheme == ElementTheme.Dark);

        UpdatePreviewAccent();
        SynchronizeSegments();
        _ = UpdateWallpaperAsync();
    }

    private void EditorUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= EditorViewModelPropertyChanged;
        _isPaletteModeSegmentedReady = false;
        _isWallpaperFitSegmentedReady = false;
    }

    private void EditorViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ViewModel.WallpaperPreviewPath):
                _ = UpdateWallpaperAsync();
                break;
            case nameof(ViewModel.UseSystemAccent):
            case nameof(ViewModel.AccentColorHex):
                UpdatePreviewAccent();
                break;
            case nameof(ViewModel.PaletteModeIndex):
            case nameof(ViewModel.WallpaperFitIndex):
                SynchronizeSegments();
                break;
        }
    }

    private void PaletteModeSegmentedLoaded(object sender, RoutedEventArgs e)
    {
        SynchronizeSegments();
        _isPaletteModeSegmentedReady = true;
    }

    private void WallpaperFitSegmentedLoaded(object sender, RoutedEventArgs e)
    {
        SynchronizeSegments();
        _isWallpaperFitSegmentedReady = true;
    }

    private void PaletteModeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isPaletteModeSegmentedReady || _isSynchronizingSegments || PaletteModeSegmented.SelectedIndex < 0)
            return;

        ViewModel.SelectPaletteMode(PaletteModeSegmented.SelectedIndex);
    }

    private void WallpaperFitSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isWallpaperFitSegmentedReady || _isSynchronizingSegments || WallpaperFitSegmented.SelectedIndex < 0)
            return;

        ViewModel.SelectWallpaperFit(WallpaperFitSegmented.SelectedIndex);
    }

    private void SynchronizeSegments()
    {
        _isSynchronizingSegments = true;

        PaletteModeSegmented.SelectedIndex = ViewModel.PaletteModeIndex;

        if (WallpaperFitSegmented != null)
            WallpaperFitSegmented.SelectedIndex = ViewModel.WallpaperFitIndex;

        _isSynchronizingSegments = false;
    }

    private void FocalPointClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string value } && Enum.TryParse<ThemeWallpaperAlignment>(value, out var alignment))
            ViewModel.SelectFocalPointCommand.Execute(alignment);
    }

    private void SurfaceColorFlyoutOpened(object sender, object e)
    {
        if (sender is not Flyout { Content: ColorPicker picker } || picker.Tag is not ThemePaletteColorOptionViewModel option)
            return;

        ShowPicker(picker, ThemeSurfacePreview.GetColor(option.Value));
    }

    private void AccentColorFlyoutOpened(object sender, object e)
    {
        if (sender is not Flyout { Content: ColorPicker picker })
            return;

        ShowPicker(picker, ThemeSurfacePreview.GetColor(ViewModel.AccentColorHex));
    }

    private void ColorFlyoutClosed(object sender, object e) => _openColorPicker = null;

    private void ShowPicker(ColorPicker picker, Color color)
    {
        _isSynchronizingColor = true;
        picker.Color = color;
        _isSynchronizingColor = false;

        _openColorPicker = picker;
    }

    private void SurfaceColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        // The picker carries its option in Tag rather than relying on the flyout
        // inheriting a data context.
        if (!IsPickerEditing(sender) || sender.Tag is not ThemePaletteColorOptionViewModel option)
            return;

        var value = ToHexWithAlpha(args.NewColor);

        if (!string.Equals(option.Value, value, StringComparison.OrdinalIgnoreCase))
            option.Value = value;
    }

    private void AccentColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (!IsPickerEditing(sender))
            return;

        var value = ToHex(args.NewColor);

        if (!string.Equals(ViewModel.AccentColorHex, value, StringComparison.OrdinalIgnoreCase))
            ViewModel.AccentColorHex = value;
    }

    private bool IsPickerEditing(ColorPicker picker)
        => !_isSynchronizingColor && ReferenceEquals(picker, _openColorPicker);

    private void WallpaperDragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            return;

        e.AcceptedOperation = DataPackageOperation.Copy;

        if (e.DragUIOverride != null)
            e.DragUIOverride.Caption = Translator.ApplicationThemeEditor_WallpaperImage;
    }

    private async void WallpaperDropped(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            return;

        var deferral = e.GetDeferral();

        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var file = items.OfType<StorageFile>().FirstOrDefault(IsSupportedImage);

            if (file == null)
                return;

            using var fileStream = await file.OpenReadAsync();
            using var readerStream = fileStream.AsStreamForRead();
            var bytes = new byte[readerStream.Length];
            await readerStream.ReadExactlyAsync(bytes);

            ViewModel.ApplyWallpaper(bytes, file.Path, file.Name);
        }
        catch (Exception exception)
        {
            ViewModel.ErrorMessage = exception.Message;
            ViewModel.IsErrorOpen = true;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static bool IsSupportedImage(StorageFile file)
    {
        var extension = Path.GetExtension(file.Name)?.ToLowerInvariant();

        return extension is ".jpg" or ".jpeg" or ".png";
    }

    private void UpdatePreviewAccent()
    {
        if (!ViewModel.UseSystemAccent && !string.IsNullOrWhiteSpace(ViewModel.AccentColorHex))
        {
            PreviewAccentColorHex = ViewModel.AccentColorHex;
            return;
        }

        PreviewAccentColorHex = Application.Current.Resources.TryGetValue("SystemAccentColor", out var value) && value is Color accent
            ? ToHex(accent)
            : DefaultAccentColorHex;
    }

    /// <summary>
    /// Loads the wallpaper for the previews and measures its average color, which the
    /// view model needs to judge the contrast of text on translucent surfaces.
    /// </summary>
    private async Task UpdateWallpaperAsync()
    {
        var uri = ThemeWallpaperPreviewPath.GetAbsoluteUri(ViewModel.WallpaperPreviewPath);

        if (uri == null)
        {
            WallpaperImageSource = null;
            ViewModel.WallpaperAverageColor = string.Empty;
            return;
        }

        WallpaperImageSource = new BitmapImage(uri);

        try
        {
            ViewModel.WallpaperAverageColor = await GetAverageColorAsync(uri);
        }
        catch (Exception)
        {
            // The contrast report is optional. Without an average color it simply stays hidden.
            ViewModel.WallpaperAverageColor = string.Empty;
        }
    }

    private static async Task<string> GetAverageColorAsync(Uri uri)
    {
        // RandomAccessStreamReference does not read file:// URIs, and a freshly picked
        // wallpaper is always one, so open the file itself.
        var file = uri.IsFile
            ? await StorageFile.GetFileFromPathAsync(uri.LocalPath)
            : await StorageFile.GetFileFromApplicationUriAsync(uri);

        using var stream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);

        var transform = new BitmapTransform { ScaledWidth = 8, ScaledHeight = 8 };
        var pixelProvider = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            transform,
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);

        var pixels = pixelProvider.DetachPixelData();

        if (pixels.Length < 4)
            return string.Empty;

        long blue = 0, green = 0, red = 0;

        for (var index = 0; index + 3 < pixels.Length; index += 4)
        {
            blue += pixels[index];
            green += pixels[index + 1];
            red += pixels[index + 2];
        }

        var count = pixels.Length / 4;

        return ToHex(Color.FromArgb(
            byte.MaxValue,
            (byte)(red / count),
            (byte)(green / count),
            (byte)(blue / count)));
    }

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static string ToHexWithAlpha(Color color) => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
}
