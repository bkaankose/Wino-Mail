using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.ApplicationModel.Core;
using Windows.Services.Store;
using Windows.System;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.UWP.Services
{
    public class StoreRatingService : IStoreRatingService
    {
        private const string RatedStorageKey = nameof(RatedStorageKey);
        private const string LatestAskedKey = nameof(LatestAskedKey);

        private readonly IConfigurationService _configurationService;
        private readonly IDialogService _dialogService;

        public StoreRatingService(IConfigurationService configurationService, IDialogService dialogService)
        {
            _configurationService = configurationService;
            _dialogService = dialogService;
        }

        private void SetRated()
            => _configurationService.SetRoaming(RatedStorageKey, true);

        private bool IsAskingThresholdExceeded()
        {
            var latestAskedDate = _configurationService.Get(LatestAskedKey, DateTime.MinValue);

            // Never asked before.
            // Set the threshold and wait for the next trigger.

            if (latestAskedDate == DateTime.MinValue)
            {
                _configurationService.Set(LatestAskedKey, DateTime.UtcNow);
            }
            else if (DateTime.UtcNow >= latestAskedDate.AddMinutes(30))
            {
                return true;
            }

            return false;
        }

        public async Task PromptRatingDialogAsync()
        {
            // Annoying.
            if (Debugger.IsAttached) return;

            // Swallow all exceptions. App should not crash in any errors.

            try
            {
                bool isRated = _configurationService.GetRoaming(RatedStorageKey, false);

                if (isRated) return;

                if (!isRated)
                {
                    if (!IsAskingThresholdExceeded()) return;

                    var ratingDialogResult = await _dialogService.ShowRatingDialogAsync();

                    if (ratingDialogResult == null)
                        return;

                    if (ratingDialogResult.DontAskAgain)
                        SetRated();

                    if (ratingDialogResult.RateWinoClicked)
                    {
                        // In case of failure of this call, we will navigate users to Store page directly.

                        try
                        {
                            await ShowPortableRatingDialogAsync();
                        }
                        catch (Exception)
                        {
                            await Launcher.LaunchUriAsync(new Uri($"ms-windows-store://review/?ProductId=9NCRCVJC50WL"));
                        }
                    }
                }
            }
            catch (Exception) { }
            finally
            {
                _configurationService.Set(LatestAskedKey, DateTime.UtcNow);
            }
        }

        private async Task ShowPortableRatingDialogAsync()
        {
            var _storeContext = StoreContext.GetDefault();

            StoreRateAndReviewResult result = await _storeContext.RequestRateAndReviewAppAsync();

            // Check status
            switch (result.Status)
            {
                case StoreRateAndReviewStatus.Succeeded:
                    if (result.WasUpdated)
                        _dialogService.InfoBarMessage(Translator.Info_ReviewSuccessTitle, Translator.Info_ReviewUpdatedMessage, Domain.Enums.InfoBarMessageType.Success);
                    else
                        _dialogService.InfoBarMessage(Translator.Info_ReviewSuccessTitle, Translator.Info_ReviewNewMessage, Domain.Enums.InfoBarMessageType.Success);

                    SetRated();
                    break;
                case StoreRateAndReviewStatus.CanceledByUser:
                    break;

                case StoreRateAndReviewStatus.NetworkError:
                    _dialogService.InfoBarMessage(Translator.Info_ReviewNetworkErrorTitle, Translator.Info_ReviewNetworkErrorMessage, Domain.Enums.InfoBarMessageType.Warning);
                    break;
                default:
                    _dialogService.InfoBarMessage(Translator.Info_ReviewUnknownErrorTitle, string.Format(Translator.Info_ReviewUnknownErrorMessage, result.ExtendedError.Message), Domain.Enums.InfoBarMessageType.Warning);
                    break;
            }
        }

        public async Task LaunchStorePageForReviewAsync()
        {
            try
            {
                await CoreApplication.GetCurrentView()?.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, async () =>
                {
                    // TODO: Get it from package info.
                    await Launcher.LaunchUriAsync(new Uri($"ms-windows-store://review/?ProductId=9NCRCVJC50WL"));
                });
            }
            catch (Exception) { }
        }
    }
}
