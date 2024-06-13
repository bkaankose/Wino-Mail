using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Toolkit.Uwp.Notifications;
using Serilog;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.Background;
using Windows.UI.Notifications;
using Wino.Core;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.UWP.Services;
using Wino.Services;

namespace Wino.Activation
{
    internal class BackgroundActivationHandler : ActivationHandler<BackgroundActivatedEventArgs>
    {
        private const string BackgroundExecutionLogTag = "[BackgroundExecution] ";

        private readonly IWinoRequestDelegator _winoRequestDelegator;
        private readonly IBackgroundSynchronizer _backgroundSynchronizer;
        private readonly INativeAppService _nativeAppService;
        private readonly IWinoRequestProcessor _winoRequestProcessor;
        private readonly IWinoSynchronizerFactory _winoSynchronizerFactory;
        private readonly IMailService _mailService;
        private ToastArguments _toastArguments;

        BackgroundTaskDeferral _deferral;
        public BackgroundActivationHandler(IWinoRequestDelegator winoRequestDelegator,
                                           IBackgroundSynchronizer backgroundSynchronizer,
                                           INativeAppService nativeAppService,
                                           IWinoRequestProcessor winoRequestProcessor,
                                           IWinoSynchronizerFactory winoSynchronizerFactory,
                                           IMailService mailService)
        {
            _winoRequestDelegator = winoRequestDelegator;
            _backgroundSynchronizer = backgroundSynchronizer;
            _nativeAppService = nativeAppService;
            _winoRequestProcessor = winoRequestProcessor;
            _winoSynchronizerFactory = winoSynchronizerFactory;
            _mailService = mailService;
        }

        protected override async Task HandleInternalAsync(BackgroundActivatedEventArgs args)
        {
            var instance = args.TaskInstance;
            var taskName = instance.Task.Name;

            instance.Canceled -= OnBackgroundExecutionCanceled;
            instance.Canceled += OnBackgroundExecutionCanceled;

            _deferral = instance.GetDeferral();

            if (taskName == BackgroundTaskService.ToastActivationTaskEx)
            {
                if (instance.TriggerDetails is ToastNotificationActionTriggerDetail toastNotificationActionTriggerDetail)
                    _toastArguments = ToastArguments.Parse(toastNotificationActionTriggerDetail.Argument);

                // All toast activation mail actions are handled here like mark as read or delete.
                // This should not launch the application on the foreground.

                // Get the action and mail item id.
                // Prepare package and send to delegator.

                if (_toastArguments.TryGetValue(Constants.ToastMailItemIdKey, out string mailItemId) &&
                    _toastArguments.TryGetValue(Constants.ToastActionKey, out MailOperation action) &&
                    _toastArguments.TryGetValue(Constants.ToastMailItemRemoteFolderIdKey, out string remoteFolderId))
                {
                    var mailItem = await _mailService.GetSingleMailItemAsync(mailItemId, remoteFolderId);

                    if (mailItem == null) return;

                    if (_nativeAppService.IsAppRunning())
                    {
                        // Just send the package. We should reflect the UI changes as well.
                        var package = new MailOperationPreperationRequest(action, mailItem);

                        await _winoRequestDelegator.ExecuteAsync(package);
                    }
                    else
                    {
                        // We need to synchronize changes without reflection the UI changes.

                        var synchronizer = _winoSynchronizerFactory.GetAccountSynchronizer(mailItem.AssignedAccount.Id);
                        var prepRequest = new MailOperationPreperationRequest(action, mailItem);

                        var requests = await _winoRequestProcessor.PrepareRequestsAsync(prepRequest);

                        foreach (var request in requests)
                        {
                            synchronizer.QueueRequest(request);
                        }

                        var options = new SynchronizationOptions()
                        {
                            Type = SynchronizationType.ExecuteRequests,
                            AccountId = mailItem.AssignedAccount.Id
                        };

                        await synchronizer.SynchronizeAsync(options);
                    }
                }
            }
            else if (taskName == BackgroundTaskService.BackgroundSynchronizationTimerTaskNameEx)
            {
                var watch = new Stopwatch();
                watch.Start();

                // Run timer based background synchronization.

                await _backgroundSynchronizer.RunBackgroundSynchronizationAsync(BackgroundSynchronizationReason.Timer);

                watch.Stop();
                Log.Information($"{BackgroundExecutionLogTag}Background synchronization is completed in {watch.Elapsed.TotalSeconds} seconds.");
            }

            instance.Canceled -= OnBackgroundExecutionCanceled;

            _deferral.Complete();
        }

        private void OnBackgroundExecutionCanceled(Windows.ApplicationModel.Background.IBackgroundTaskInstance sender, Windows.ApplicationModel.Background.BackgroundTaskCancellationReason reason)
        {
            Log.Error($"{BackgroundExecutionLogTag} ({sender.Task.Name}) Background task is canceled. Reason -> {reason}");

            _deferral?.Complete();
        }

        protected override bool CanHandleInternal(BackgroundActivatedEventArgs args)
        {
            var instance = args.TaskInstance;
            var taskName = instance.Task.Name;

            if (taskName == BackgroundTaskService.ToastActivationTaskEx)
            {
                // User clicked Mark as Read or Delete in toast notification.
                // MailId and Action must present in the arguments.

                return true;

                //if (instance.TriggerDetails is ToastNotificationActionTriggerDetail toastNotificationActionTriggerDetail)
                //{
                //    _toastArguments = ToastArguments.Parse(toastNotificationActionTriggerDetail.Argument);

                //    return
                //        _toastArguments.Contains(Constants.ToastMailItemIdKey) &&
                //        _toastArguments.Contains(Constants.ToastActionKey);
                //}

            }
            else if (taskName == BackgroundTaskService.BackgroundSynchronizationTimerTaskNameEx)
            {
                // This is timer based background synchronization.


                return true;
            }

            return false;
        }
    }
}
