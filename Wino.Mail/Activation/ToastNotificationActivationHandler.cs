using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Toolkit.Uwp.Notifications;
using Serilog;
using Windows.ApplicationModel.Activation;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Messages.Accounts;

namespace Wino.Activation
{
    /// <summary>
    /// This handler will only handle the toasts that runs on foreground.
    /// Background executions are not handled here like mark as read or delete.
    /// </summary>
    internal class ToastNotificationActivationHandler : ActivationHandler<ToastNotificationActivatedEventArgs>
    {
        private readonly INativeAppService _nativeAppService;
        private readonly IMailService _mailService;
        private readonly IFolderService _folderService;

        private ToastArguments _toastArguments;

        public ToastNotificationActivationHandler(INativeAppService nativeAppService,
                                                  IMailService mailService,
                                                  IFolderService folderService)
        {
            _nativeAppService = nativeAppService;
            _mailService = mailService;
            _folderService = folderService;
        }

        protected override async Task HandleInternalAsync(ToastNotificationActivatedEventArgs args)
        {
            // Create the mail item navigation event.
            // If the app is running, it'll be picked up by the Messenger.
            // Otherwise we'll save it and handle it when the shell loads all accounts.

            // Parse the mail unique id and perform above actions.
            if (Guid.TryParse(_toastArguments[Constants.ToastMailItemIdKey], out Guid mailItemUniqueId))
            {
                var account = await _mailService.GetMailAccountByUniqueIdAsync(mailItemUniqueId).ConfigureAwait(false);
                if (account == null) return;

                var mailItem = await _mailService.GetSingleMailItemAsync(mailItemUniqueId).ConfigureAwait(false);
                if (mailItem == null) return;

                var message = new AccountMenuItemExtended(mailItem.AssignedFolder.Id, mailItem);

                // Delegate this event to LaunchProtocolService so app shell can pick it up on launch if app doesn't work.
                var launchProtocolService = App.Current.Services.GetService<ILaunchProtocolService>();
                launchProtocolService.LaunchParameter = message;

                // Send the messsage anyways. Launch protocol service will be ignored if the message is picked up by subscriber shell.
                WeakReferenceMessenger.Default.Send(message);
            }
        }

        protected override bool CanHandleInternal(ToastNotificationActivatedEventArgs args)
        {
            try
            {
                _toastArguments = ToastArguments.Parse(args.Argument);

                return
                    _toastArguments.Contains(Constants.ToastMailItemIdKey) &&
                    _toastArguments.Contains(Constants.ToastActionKey);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Couldn't handle parsing toast notification arguments for foreground navigate.");
            }

            return false;
        }
    }
}
