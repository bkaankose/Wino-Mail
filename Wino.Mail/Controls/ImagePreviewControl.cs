using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Fernandezja.ColorHashSharp;
using Microsoft.Extensions.DependencyInjection;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Shapes;
using Wino.Core.Domain.Interfaces;
using Wino.Core.UWP;
using Wino.Core.UWP.Services;

namespace Wino.Controls;

public partial class ImagePreviewControl : Control
{
    private const string PART_EllipseInitialsGrid = "EllipseInitialsGrid";
    private const string PART_InitialsTextBlock = "InitialsTextBlock";
    private const string PART_KnownHostImage = "KnownHostImage";
    private const string PART_Ellipse = "Ellipse";

    #region Dependency Properties

    public static readonly DependencyProperty FromNameProperty = DependencyProperty.Register(nameof(FromName), typeof(string), typeof(ImagePreviewControl), new PropertyMetadata(string.Empty, OnAddressInformationChanged));
    public static readonly DependencyProperty FromAddressProperty = DependencyProperty.Register(nameof(FromAddress), typeof(string), typeof(ImagePreviewControl), new PropertyMetadata(string.Empty, OnAddressInformationChanged));
    public static readonly DependencyProperty SenderContactPictureProperty = DependencyProperty.Register(nameof(SenderContactPicture), typeof(string), typeof(ImagePreviewControl), new PropertyMetadata(string.Empty, new PropertyChangedCallback(OnAddressInformationChanged)));

    /// <summary>
    /// Gets or sets base64 string of the sender contact picture.
    /// </summary>
    public string SenderContactPicture
    {
        get { return (string)GetValue(SenderContactPictureProperty); }
        set { SetValue(SenderContactPictureProperty, value); }
    }

    public string FromName
    {
        get { return (string)GetValue(FromNameProperty); }
        set { SetValue(FromNameProperty, value); }
    }

    public string FromAddress
    {
        get { return (string)GetValue(FromAddressProperty); }
        set { SetValue(FromAddressProperty, value); }
    }

    #endregion

    private Ellipse Ellipse;
    private Grid InitialsGrid;
    private TextBlock InitialsTextblock;
    private Image KnownHostImage;
    private Border FaviconSquircle;
    private Image FaviconImage;
    private bool isFavicon;
    private CancellationTokenSource contactPictureLoadingCancellationTokenSource;
    private IPreferencesService _preferencesService;
    private readonly IThumbnailService _thumbnailService;

    public ImagePreviewControl()
    {
        DefaultStyleKey = nameof(ImagePreviewControl);
        _thumbnailService = WinoApplication.Current.Services.GetService<IThumbnailService>();
        _preferencesService = WinoApplication.Current.Services.GetService<IPreferencesService>();
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        InitialsGrid = GetTemplateChild(PART_EllipseInitialsGrid) as Grid;
        InitialsTextblock = GetTemplateChild(PART_InitialsTextBlock) as TextBlock;
        KnownHostImage = GetTemplateChild(PART_KnownHostImage) as Image;
        Ellipse = GetTemplateChild(PART_Ellipse) as Ellipse;
        FaviconSquircle = GetTemplateChild("FaviconSquircle") as Border;
        FaviconImage = GetTemplateChild("FaviconImage") as Image;

        UpdateInformation();
    }

    private static void OnAddressInformationChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
    {
        if (obj is ImagePreviewControl control)
            control.UpdateInformation();
    }

    private async void UpdateInformation()
    {
        if ((KnownHostImage == null && FaviconSquircle == null) || InitialsGrid == null || InitialsTextblock == null || (string.IsNullOrEmpty(FromName) && string.IsNullOrEmpty(FromAddress)))
            return;

        // Cancel active image loading if exists.
        if (!contactPictureLoadingCancellationTokenSource?.IsCancellationRequested ?? false)
        {
            contactPictureLoadingCancellationTokenSource.Cancel();
        }

        string contactPicture = SenderContactPicture;
        isFavicon = false;
        if (string.IsNullOrEmpty(contactPicture) && !string.IsNullOrEmpty(FromAddress))
        {
            if (_preferencesService.IsGravatarEnabled)
            {
                contactPicture = await _thumbnailService.TryGetGravatarBase64Async(FromAddress);
            }
            if (string.IsNullOrEmpty(contactPicture) && _preferencesService.IsFaviconEnabled)
            {
                var host = _thumbnailService.GetHost(FromAddress);
                contactPicture = await _thumbnailService.TryGetFaviconBase64Async(host);
                if (!string.IsNullOrEmpty(contactPicture))
                    isFavicon = true;
            }
        }

        if (!string.IsNullOrEmpty(contactPicture))
        {
            if (isFavicon && FaviconSquircle != null && FaviconImage != null)
            {
                // Show favicon in squircle
                FaviconSquircle.Visibility = Visibility.Visible;
                InitialsGrid.Visibility = Visibility.Collapsed;
                KnownHostImage.Visibility = Visibility.Collapsed;
                FaviconImage.Source = await GetBitmapImageAsync(contactPicture);
            }
            else
            {
                // Show normal avatar (tondo)
                FaviconSquircle.Visibility = Visibility.Collapsed;
                KnownHostImage.Visibility = Visibility.Collapsed;
                InitialsGrid.Visibility = Visibility.Visible;
                contactPictureLoadingCancellationTokenSource = new CancellationTokenSource();
                try
                {
                    var brush = await GetContactImageBrushAsync(contactPicture);
                    if (!contactPictureLoadingCancellationTokenSource?.Token.IsCancellationRequested ?? false)
                    {
                        Ellipse.Fill = brush;
                        InitialsTextblock.Text = string.Empty;
                    }
                }
                catch (Exception)
                {
                    Debugger.Break();
                }
            }
        }
        else
        {
            FaviconSquircle.Visibility = Visibility.Collapsed;
            KnownHostImage.Visibility = Visibility.Collapsed;
            InitialsGrid.Visibility = Visibility.Visible;
            var colorHash = new ColorHash();
            var rgb = colorHash.Rgb(FromAddress);
            Ellipse.Fill = new SolidColorBrush(Color.FromArgb(rgb.A, rgb.R, rgb.G, rgb.B));
            InitialsTextblock.Text = ExtractInitialsFromName(FromName);
        }
    }

    private async Task<ImageBrush> GetContactImageBrushAsync(string base64)
    {
        // Load the image from base64 string.
        var bitmapImage = new BitmapImage();

        var imageArray = Convert.FromBase64String(base64);
        var imageStream = new MemoryStream(imageArray);
        var randomAccessImageStream = imageStream.AsRandomAccessStream();

        randomAccessImageStream.Seek(0);

        await bitmapImage.SetSourceAsync(randomAccessImageStream);

        return new ImageBrush() { ImageSource = bitmapImage };
    }

    private async Task<BitmapImage> GetBitmapImageAsync(string base64)
    {
        var bitmapImage = new BitmapImage();
        var imageArray = Convert.FromBase64String(base64);
        var imageStream = new MemoryStream(imageArray);
        var randomAccessImageStream = imageStream.AsRandomAccessStream();
        randomAccessImageStream.Seek(0);
        await bitmapImage.SetSourceAsync(randomAccessImageStream);
        return bitmapImage;
    }

    public string ExtractInitialsFromName(string name)
    {
        // Change from name to from address in case of name doesn't exists.
        if (string.IsNullOrEmpty(name))
        {
            name = FromAddress;
        }

        // first remove all: punctuation, separator chars, control chars, and numbers (unicode style regexes)
        string initials = Regex.Replace(name, @"[\p{P}\p{S}\p{C}\p{N}]+", "");

        // Replacing all possible whitespace/separator characters (unicode style), with a single, regular ascii space.
        initials = Regex.Replace(initials, @"\p{Z}+", " ");

        // Remove all Sr, Jr, I, II, III, IV, V, VI, VII, VIII, IX at the end of names
        initials = Regex.Replace(initials.Trim(), @"\s+(?:[JS]R|I{1,3}|I[VX]|VI{0,3})$", "", RegexOptions.IgnoreCase);

        // Extract up to 2 initials from the remaining cleaned name.
        initials = Regex.Replace(initials, @"^(\p{L})[^\s]*(?:\s+(?:\p{L}+\s+(?=\p{L}))?(?:(\p{L})\p{L}*)?)?$", "$1$2").Trim();

        if (initials.Length > 2)
        {
            // Worst case scenario, everything failed, just grab the first two letters of what we have left.
            initials = initials.Substring(0, 2);
        }

        return initials.ToUpperInvariant();
    }
}
