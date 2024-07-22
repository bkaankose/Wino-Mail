using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Windows.ApplicationModel;
using Windows.ApplicationModel.AppService;
using Windows.Foundation.Collections;
using Wino.Core.Authenticators;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Integration.Processors;
using Wino.Core.Services;
using Wino.Core.Synchronizers;
using Wino.Messaging;
using Wino.Messaging.Enums;
using Wino.Messaging.Server;

namespace Wino.Server
{
    public class ServerContext :
        IRecipient<AccountCreatedMessage>,
        IRecipient<AccountUpdatedMessage>,
        IRecipient<AccountRemovedMessage>,
        IRecipient<DraftCreated>,
        IRecipient<DraftFailed>,
        IRecipient<DraftMapped>,
        IRecipient<FolderRenamed>,
        IRecipient<FolderSynchronizationEnabled>,
        IRecipient<MailAddedMessage>,
        IRecipient<MailDownloadedMessage>,
        IRecipient<MailRemovedMessage>,
        IRecipient<MailUpdatedMessage>,
        IRecipient<MergedInboxRenamed>
    {
        private static object connectionLock = new object();

        private AppServiceConnection connection = null;

        private readonly IDatabaseService _databaseService;
        private readonly IApplicationConfiguration _applicationFolderConfiguration;

        public ServerContext(IDatabaseService databaseService, IApplicationConfiguration applicationFolderConfiguration)
        {
            _databaseService = databaseService;
            _applicationFolderConfiguration = applicationFolderConfiguration;

            WeakReferenceMessenger.Default.RegisterAll(this);
        }

        #region Message Handlers

        public async void Receive(MailAddedMessage message) => await SendMessageAsync(MessageType.UIMessage, message);

        public async void Receive(AccountCreatedMessage message) => await SendMessageAsync(MessageType.UIMessage, message);

        public async void Receive(AccountUpdatedMessage message) => await SendMessageAsync(MessageType.UIMessage, message);

        public async void Receive(AccountRemovedMessage message) => await SendMessageAsync(MessageType.UIMessage, message);

        public async void Receive(DraftCreated message) => await SendMessageAsync(MessageType.UIMessage, message);

        public async void Receive(DraftFailed message) => await SendMessageAsync(MessageType.UIMessage, message);

        public async void Receive(DraftMapped message) => await SendMessageAsync(MessageType.UIMessage, message);

        public async void Receive(FolderRenamed message) => await SendMessageAsync(MessageType.UIMessage, message);

        public async void Receive(FolderSynchronizationEnabled message) => await SendMessageAsync(MessageType.UIMessage, message);

        public async void Receive(MailDownloadedMessage message) => await SendMessageAsync(MessageType.UIMessage, message);

        public async void Receive(MailRemovedMessage message) => await SendMessageAsync(MessageType.UIMessage, message);

        public async void Receive(MailUpdatedMessage message) => await SendMessageAsync(MessageType.UIMessage, message);

        public async void Receive(MergedInboxRenamed message) => await SendMessageAsync(MessageType.UIMessage, message);

        #endregion

        private string GetAppPackagFamilyName()
        {
            // If running as a standalone app, Package will throw exception.
            // Return hardcoded value for debugging purposes.
            // Connection will not be available in this case.

            try
            {
                return Package.Current.Id.FamilyName;
            }
            catch (Exception)
            {
                return "Debug.Wino.Server.FamilyName";
            }
        }

        /// <summary>
        /// Open connection to UWP app service
        /// </summary>
        public async Task InitializeAppServiceConnectionAsync()
        {
            if (connection != null) DisposeConnection();

            connection = new AppServiceConnection
            {
                AppServiceName = "WinoInteropService",
                PackageFamilyName = GetAppPackagFamilyName()
            };

            connection.RequestReceived += OnWinRTMessageReceived;
            connection.ServiceClosed += OnConnectionClosed;

            AppServiceConnectionStatus status = await connection.OpenAsync();

            if (status != AppServiceConnectionStatus.Success)
            {
                // TODO: Handle connection error

                DisposeConnection();
            }
        }

        public async Task TestOutlookSynchronizer()
        {
            var accountService = App.Current.Services.GetService<IAccountService>();

            var accs = await accountService.GetAccountsAsync();
            var acc = accs.ElementAt(0);

            var authenticator = App.Current.Services.GetService<OutlookAuthenticator>();
            var processor = App.Current.Services.GetService<IOutlookChangeProcessor>();

            var sync = new OutlookSynchronizer(acc, authenticator, processor);

            var options = new SynchronizationOptions()
            {
                AccountId = acc.Id,
                Type = Core.Domain.Enums.SynchronizationType.Full
            };

            var result = await sync.SynchronizeAsync(options);
        }

        /// <summary>
        /// Disposes current connection to UWP app service.
        /// </summary>
        private void DisposeConnection()
        {
            lock (connectionLock)
            {
                if (connection == null) return;

                connection.RequestReceived -= OnWinRTMessageReceived;
                connection.ServiceClosed -= OnConnectionClosed;

                connection.Dispose();
                connection = null;
            }
        }

        /// <summary>
        /// Sends a serialized object to UWP application if connection exists with given type.
        /// </summary>
        /// <param name="messageType">Type of the message.</param>
        /// <param name="message">IServerMessage object that will be serialized.</param>
        /// <returns></returns>
        /// <exception cref="ArgumentException">When the message is not IServerMessage.</exception>
        private async Task SendMessageAsync(MessageType messageType, object message)
        {
            if (connection == null) return;

            if (message is not IServerMessage serverMessage)
                throw new ArgumentException("Server message must be a type of IServerMessage");

            string json = JsonSerializer.Serialize(message);

            var set = new ValueSet
            {
                { MessageConstants.MessageTypeKey, (int)messageType },
                { MessageConstants.MessageDataKey, json },
                { MessageConstants.MessageDataTypeKey, message.GetType().Name }
            };

            Debug.WriteLine($"S: {messageType} ({message.GetType().Name})");
            await connection.SendMessageAsync(set);
        }

        private void OnConnectionClosed(AppServiceConnection sender, AppServiceClosedEventArgs args)
        {
            // TODO: Handle connection closed.

            // UWP app might've been terminated or suspended.
            // At this point, we must keep active synchronizations going, but connection is lost.
            // As long as this process is alive, database will be kept updated, but no messages will be sent.

            DisposeConnection();
        }

        private void OnWinRTMessageReceived(AppServiceConnection sender, AppServiceRequestReceivedEventArgs args)
        {
            // TODO: Handle incoming messages from UWP / WINUI Application.

            if (args.Request.Message.TryGetValue(MessageConstants.MessageTypeKey, out object messageTypeObject) && messageTypeObject is int messageTypeInt)
            {
                var messageType = (MessageType)messageTypeInt;

                if (args.Request.Message.TryGetValue(MessageConstants.MessageDataKey, out object messageDataObject) && messageDataObject is string messageJson)
                {
                    switch (messageType)
                    {
                        case MessageType.UIMessage:
                            if (!args.Request.Message.TryGetValue(MessageConstants.MessageDataTypeKey, out object dataTypeObject) || dataTypeObject is not string dataTypeName)
                                throw new ArgumentException("Message data type is missing.");

                            HandleUIMessage(messageJson, dataTypeName);
                            break;
                        case MessageType.ServerAction:
                            HandleServerAction(messageJson);
                            break;
                        default:
                            break;
                    }
                }
            }
        }

        private void HandleServerAction(string messageJson)
        {

        }

        private void HandleUIMessage(string messageJson, string typeName)
        {

        }
    }
}
