using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.Graph.Models;
using Microsoft.Xaml.Interactivity;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Graphics.Display;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Documents;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using Wino.Core.Domain.Models.MailItem;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace Wino.Controls
{
    /// <summary>
    /// Used to render and animate Mail Item drag effects
    /// </summary>
    public sealed partial class MailItemDragPopup : UserControl
    {
        /// <summary>
        /// One or more mail items that are being dragged
        /// </summary>
        private IMailItem[] _mailItems;

        public enum HideAnimation
        {
            FadeOut,
            DroppedIn
        }


        public string DisplayMode
        {
            get { return (string)GetValue(DisplayModeProperty); }
            set { SetValue(DisplayModeProperty, value); }
        }

        public static readonly DependencyProperty DisplayModeProperty =
            DependencyProperty.Register("DisplayMode", typeof(string), typeof(MailItemDragPopup), new PropertyMetadata("all", onDisplayModeChanged));

        private static void onDisplayModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var obj = (MailItemDragPopup)d;
            if ((string)e.NewValue == "single")
            {
                obj.SingleItemGrid.Opacity = 1;
                obj.MultiItemGrid.Opacity = 0;
            } else if ((string)e.NewValue == "multi")
            {
                obj.SingleItemGrid.Opacity = 0;
                obj.MultiItemGrid.Opacity = 1;
            }
        }

        /// <summary>
        /// This function is called when we first start dragging a MailItem. It initializes this component with the proper
        /// text/elements/formatting and returns a BitmapImage representation of this control. That BitmapImage can then
        /// replace the existing drag image that UWP creates by default.
        /// </summary>
        /// <param name="mailItems"></param>
        /// <param name="thumbnail">Sender preview image shown</param>
        /// <param name="caption">Caption shown below drag image</param>
        /// <returns></returns>
        public async Task<BitmapImage> InitializeAndRenderDragPopupAsync(IMailItem[] mailItems, BitmapImage thumbnail, string caption = null, string captionIcon = null)
        {
            _mailItems = mailItems;
            if (mailItems.Length == 0)
            {
                return new BitmapImage(); // this should never be hit!
            }

            DisplayMode = _mailItems.Length > 1 ? "multi" : "single";

            if (_mailItems.Length == 2)
            {
                MultiDragPopup3.Opacity = 0;
            } else if (_mailItems.Length > 2)
            {
                MultiDragPopup3.Opacity = 0.2;
            }

            DragPopupScaleTransform.ScaleX = 1;
            DragPopupScaleTransform.ScaleY = 1;

            if (DisplayMode == "single")
            {
                var item = _mailItems[0];
                DragPopupImage.FromAddress = item.FromAddress;
                DragPopupImage.FromName = item.FromName;
                DragPopupHeaderText.Text = item.FromName;
                DragPopupSubjectText.Text = item.Subject;

                if (caption == null)
                {
                    SingleSelectCaption.Visibility = Visibility.Collapsed;
                }
                else
                {
                    SingleSelectCaption.Visibility = Visibility.Visible;
                    SingleSelectCaptionText.Text = caption;
                }
                if (captionIcon == null)
                {
                    SingleSelectCaptionIcon.Visibility = Visibility.Collapsed;
                    SingleSelectCaptionStackpanel.Spacing = 0;
                }
                else
                {
                    SingleSelectCaptionIcon.Visibility = Visibility.Visible;
                    SingleSelectCaptionIcon.Glyph = captionIcon;
                    SingleSelectCaptionStackpanel.Spacing = 6;
                }

                // Unfortunately rendering the thumbnail image isn't that simple since the PreviousImageControl can't
                // load in time for the DragUI image to generate. To fix this, we grab the already rendered BitMap image
                // from the selected mail item and set it directly.
                if (thumbnail != null)
                {
                    DragPopupImage.SetThumbnailImage(thumbnail);
                }
            }
            else
            {
                var item = _mailItems[0];

                DragPopupImage2.FromAddress = item.FromAddress;
                DragPopupImage2.FromName = item.FromName;
                DragPopupHeaderText2.Text = item.FromName;
                DragPopupSubjectText2.Text = item.Subject;

                if (caption == null)
                {
                    MultiSelectCaptionText.Text = $"{mailItems.Length} Items";
                }
                else
                {
                    MultiSelectCaptionText.Text = caption;
                }
                if (captionIcon == null)
                {
                    MultiSelectCaptionIcon.Visibility = Visibility.Collapsed;
                    MultiSelectCaptionStackpanel.Spacing = 0;
                }
                else
                {
                    MultiSelectCaptionIcon.Visibility = Visibility.Visible;
                    MultiSelectCaptionIcon.Glyph = captionIcon;
                    MultiSelectCaptionStackpanel.Spacing = 6;
                }

                // Unfortunetally rendering the thumbnail image isn't that simple since the PreviousImageControl can't
                // load in time for the DragUI image to generate. To fix this, we grab the already rendered BitMap image
                // from the selected mail item and set it directly.
                // This is (probably) more efficient too...
                if (thumbnail != null)
                {
                    DragPopupImage2.SetThumbnailImage(thumbnail);
                }
            }

            return await GenerateBitmap();
        }

        public async Task<BitmapImage> UpdateCaption(string caption, string captionIcon = null)
        {
            if (DisplayMode == "single")
            {
                if (caption == null)
                {
                    SingleSelectCaption.Visibility = Visibility.Collapsed;
                }
                else
                {
                    SingleSelectCaption.Visibility = Visibility.Visible;
                    SingleSelectCaptionText.Text = caption;
                }
                if (captionIcon == null)
                {
                    SingleSelectCaptionIcon.Visibility = Visibility.Collapsed;
                    SingleSelectCaptionStackpanel.Spacing = 0;
                }
                else
                {
                    SingleSelectCaptionIcon.Visibility = Visibility.Visible;
                    SingleSelectCaptionIcon.Glyph = captionIcon;
                    SingleSelectCaptionStackpanel.Spacing = 6;
                }
            }
            else
            {
                if (caption == null)
                {
                    MultiSelectCaptionText.Text = $"{_mailItems.Length} Items";
                }
                else
                {
                    MultiSelectCaptionText.Text = caption;
                }
                if (captionIcon == null)
                {
                    MultiSelectCaptionIcon.Visibility = Visibility.Collapsed;
                    MultiSelectCaptionStackpanel.Spacing = 0;
                }
                else
                {
                    MultiSelectCaptionIcon.Visibility = Visibility.Visible;
                    MultiSelectCaptionIcon.Glyph = captionIcon;
                    MultiSelectCaptionStackpanel.Spacing = 6;
                }
            }

            return await GenerateBitmap();
        }

        /// <summary>
        /// Returns a Bitmap image of the drag popup element that can be applied to the DragUI using <c>e.DragUIOverride.SetContentFromBitmapImage</c>.
        /// </summary>
        /// <returns></returns>
        public async Task<BitmapImage> GenerateBitmap()
        {
            // which grid is sent to the RenderTargetBitmap
            Grid renderedGrid;

            if (DisplayMode == "single")
            {
                renderedGrid = SingleItemGrid;
            }
            else
            {
                renderedGrid = MultiItemGrid;
            }

            var bmp = new RenderTargetBitmap();

            await bmp.RenderAsync(renderedGrid);

            InMemoryRandomAccessStream stream = new InMemoryRandomAccessStream();
            var buffer = await bmp.GetPixelsAsync();
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
        /// Called when the package is dragged and dropped at location
        /// </summary>
        /// <param name="e"></param>
        public void MoveToMouse(DragEventArgs e)
        {
            var pos = e.GetPosition((Canvas)Parent);
            Canvas.SetLeft(this, pos.X);
            Canvas.SetTop(this, pos.Y);
        }

        /// <summary>
        /// Hide the DragPopup with a specified animation
        /// </summary>
        /// <param name="animation"></param>
        public void Hide(HideAnimation animation)
        {
            Opacity = 1;
            switch (animation)
            {
                case HideAnimation.FadeOut:
                    DragPopupFadeOutAnimation.Begin();
                    break;
                case HideAnimation.DroppedIn:
                    DragPopupDroppedAnimation.Begin();
                    break;
                default:
                    break;
            }
        }

        public MailItemDragPopup()
        {
            this.InitializeComponent();
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            // set background opacity
            DragPopup.Background.Opacity = 0.85;
        }
    }
}
