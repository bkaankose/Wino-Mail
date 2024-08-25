﻿using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Fernandezja.ColorHashSharp;
using Windows.Graphics.Display;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Shapes;
using Wino.Core.Services;

namespace Wino.Controls
{
    public class ImagePreviewControl : Control
    {
        private const string PART_EllipseInitialsGrid = "EllipseInitialsGrid";
        private const string PART_InitialsTextBlock = "InitialsTextBlock";
        private const string PART_KnownHostImage = "KnownHostImage";
        private const string PART_Ellipse = "Ellipse";

        #region Dependency Properties

        public static readonly DependencyProperty FromNameProperty = DependencyProperty.Register(nameof(FromName), typeof(string), typeof(ImagePreviewControl), new PropertyMetadata(string.Empty, OnAddressInformationChanged));
        public static readonly DependencyProperty FromAddressProperty = DependencyProperty.Register(nameof(FromAddress), typeof(string), typeof(ImagePreviewControl), new PropertyMetadata(string.Empty, OnAddressInformationChanged));
        public static readonly DependencyProperty IsKnownProperty = DependencyProperty.Register(nameof(IsKnown), typeof(bool), typeof(ImagePreviewControl), new PropertyMetadata(false));

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

        public bool IsKnown
        {
            get { return (bool)GetValue(IsKnownProperty); }
            set { SetValue(IsKnownProperty, value); }
        }



        #endregion

        private Ellipse Ellipse;
        private Grid InitialsGrid;
        private TextBlock InitialsTextblock;
        private Image KnownHostImage;

        public ImagePreviewControl()
        {
            DefaultStyleKey = nameof(ImagePreviewControl);
        }

        protected override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            InitialsGrid = GetTemplateChild(PART_EllipseInitialsGrid) as Grid;
            InitialsTextblock = GetTemplateChild(PART_InitialsTextBlock) as TextBlock;
            KnownHostImage = GetTemplateChild(PART_KnownHostImage) as Image;
            Ellipse = GetTemplateChild(PART_Ellipse) as Ellipse;

            UpdateInformation();
        }

        private static void OnAddressInformationChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
        {
            if (obj is ImagePreviewControl control)
                control.UpdateInformation();
        }

        private void UpdateInformation()
        {
            if (KnownHostImage == null || InitialsGrid == null || InitialsTextblock == null || (string.IsNullOrEmpty(FromName) && string.IsNullOrEmpty(FromAddress)))
                return;

            var host = ThumbnailService.GetHost(FromAddress);

            if (!string.IsNullOrEmpty(host))
            {
                var tuple = ThumbnailService.CheckIsKnown(host);

                IsKnown = tuple.Item1;
                host = tuple.Item2;
            }

            if (IsKnown)
            {
                // Unrealize others.

                KnownHostImage.Visibility = Visibility.Visible;
                InitialsGrid.Visibility = Visibility.Collapsed;

                // Apply company logo.
                KnownHostImage.Source = new BitmapImage(new Uri(ThumbnailService.GetKnownHostImage(host)));
            }
            else
            {
                KnownHostImage.Visibility = Visibility.Collapsed;
                InitialsGrid.Visibility = Visibility.Visible;

                var colorHash = new ColorHash();
                var rgb = colorHash.Rgb(FromAddress);

                Ellipse.Fill = new SolidColorBrush(Color.FromArgb(rgb.A, rgb.R, rgb.G, rgb.B));

                InitialsTextblock.Text = ExtractInitialsFromName(FromName);
            }
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


        /// <summary>
        /// Directly get preview image rather than re-searching it through the Thumbnail service
        /// </summary>
        /// <returns><see langword="Null"/> if there is no image set</returns>
        public async Task<BitmapImage> GetKnownHostImageAsync()
        {
            if (KnownHostImage == null || KnownHostImage.Visibility == Visibility.Collapsed) { return null; }

            var bmp = new RenderTargetBitmap();

            await bmp.RenderAsync(KnownHostImage);

            InMemoryRandomAccessStream stream = new InMemoryRandomAccessStream();
            var buffer = await bmp.GetPixelsAsync();
            //  await stream.ReadAsync(buffer, (uint)buffer.Length, InputStreamOptions.None);
            BitmapImage img = new BitmapImage();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Straight,
                (uint)bmp.PixelWidth,
                (uint)bmp.PixelHeight,
                DisplayInformation.GetForCurrentView().LogicalDpi,
                DisplayInformation.GetForCurrentView().LogicalDpi,
                buffer.ToArray());
            await encoder.FlushAsync();
            await img.SetSourceAsync(stream);

            return img;
        }

        /// <summary>
        /// Set thumbnail image directly using a bitmap rather than going through ThumbnailService
        /// </summary>
        /// <param name="img"></param>
        public void SetThumbnailImage(BitmapImage img)
        {
            if (KnownHostImage == null)
            {
                KnownHostImage = new Image();
            }
            KnownHostImage.Source = img;
        }
    }
}
