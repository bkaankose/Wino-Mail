using Windows.UI.Xaml.Controls;
using Wino.Core.Domain.Interfaces;

namespace Wino.Dialogs
{
    public sealed partial class StoreRatingDialog : ContentDialog, IStoreRatingDialog
    {
        public bool DontAskAgain { get; set; }
        public bool RateWinoClicked { get; set; }

        public StoreRatingDialog()
        {
            this.InitializeComponent();
        }

        private void RateClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            RateWinoClicked = true;
        }
    }
}
