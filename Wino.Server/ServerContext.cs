using System;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.AppService;
using Windows.Foundation.Collections;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Interfaces;
using Wino.Messaging;
using Wino.Messaging.Enums;
using Wino.Messaging.Server;

namespace Wino.Server
{
    public class ServerContext
    {
        private static object connectionLock = new object();

        private AppServiceConnection connection = null;

        public ServerContext()
        {
            InitializeAppServiceConnection();
        }

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
        public async void InitializeAppServiceConnection()
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

        public Task SendTestMessageAsync()
        {
            var message = new MailAddedMessage(new MailCopy());
            return SendMessageAsync(MessageType.UIMessage, message);
        }

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
            // TODO: Handle incoming messages from UWP/WINUI Application.
        }

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
    }
}
