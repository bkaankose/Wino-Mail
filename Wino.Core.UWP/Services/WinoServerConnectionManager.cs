using System;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Windows.ApplicationModel;
using Windows.ApplicationModel.AppService;
using Windows.Foundation.Collections;
using Windows.Foundation.Metadata;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Requests;
using Wino.Core.Integration.Json;
using Wino.Messaging;
using Wino.Messaging.Enums;
using Wino.Messaging.UI;

namespace Wino.Core.UWP.Services
{
    public class WinoServerConnectionManager : IWinoServerConnectionManager<AppServiceConnection>
    {
        public event EventHandler<WinoServerConnectionStatus> StatusChanged;

        private WinoServerConnectionStatus status;

        public WinoServerConnectionStatus Status
        {
            get { return status; }
            private set
            {
                status = value;
                StatusChanged?.Invoke(this, value);
            }
        }

        private AppServiceConnection _connection;
        public AppServiceConnection Connection
        {
            get { return _connection; }
            set
            {
                if (_connection != null)
                {
                    _connection.RequestReceived -= ServerMessageReceived;
                    _connection.ServiceClosed -= ServerDisconnected;
                }

                _connection = value;

                if (value == null)
                {
                    Status = WinoServerConnectionStatus.Disconnected;
                }
                else
                {
                    value.RequestReceived += ServerMessageReceived;
                    value.ServiceClosed += ServerDisconnected;

                    Status = WinoServerConnectionStatus.Connected;
                }
            }
        }

        private readonly JsonSerializerOptions _serverJsonServerSerializer = new JsonSerializerOptions
        {
            TypeInfoResolver = new ServerRequestTypeInfoResolver()
        };

        public async Task<bool> ConnectAsync()
        {
            if (Status == WinoServerConnectionStatus.Connected) return true;

            if (ApiInformation.IsApiContractPresent("Windows.ApplicationModel.FullTrustAppContract", 1, 0))
            {
                try
                {
                    Status = WinoServerConnectionStatus.Connecting;

                    await FullTrustProcessLauncher.LaunchFullTrustProcessForCurrentAppAsync();

                    // If the server connection is success, Status will be updated to Connected by App.xaml.cs OnBackgroundActivated.
                }
                catch (Exception)
                {
                    Status = WinoServerConnectionStatus.Failed;
                    return false;
                }

                return true;
            }

            return false;
        }

        public async Task<bool> DisconnectAsync()
        {
            if (Connection == null || Status == WinoServerConnectionStatus.Disconnected) return true;

            // TODO: Send disconnect message to the fulltrust process.

            return true;
        }

        public async Task InitializeAsync()
        {
            var isConnectionSuccessfull = await ConnectAsync();

            // TODO: Log connection status
        }

        private void ServerMessageReceived(AppServiceConnection sender, AppServiceRequestReceivedEventArgs args)
        {
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
                        default:
                            break;
                    }
                }
            }
        }

        /// <summary>
        /// Unpacks IServerMessage objects and delegate it to Messenger for UI to process.
        /// </summary>
        /// <param name="messageJson">Message data in json format.</param>
        private void HandleUIMessage(string messageJson, string typeName)
        {
            switch (typeName)
            {
                case nameof(MailAddedMessage):
                    WeakReferenceMessenger.Default.Send(JsonSerializer.Deserialize<MailAddedMessage>(messageJson));
                    break;
                case nameof(MailDownloadedMessage):
                    WeakReferenceMessenger.Default.Send(JsonSerializer.Deserialize<MailDownloadedMessage>(messageJson));
                    break;
                case nameof(MailRemovedMessage):
                    WeakReferenceMessenger.Default.Send(JsonSerializer.Deserialize<MailRemovedMessage>(messageJson));
                    break;
                case nameof(MailUpdatedMessage):
                    WeakReferenceMessenger.Default.Send(JsonSerializer.Deserialize<MailUpdatedMessage>(messageJson));
                    break;
                case nameof(AccountCreatedMessage):
                    WeakReferenceMessenger.Default.Send(JsonSerializer.Deserialize<AccountCreatedMessage>(messageJson));
                    break;
                case nameof(AccountRemovedMessage):
                    WeakReferenceMessenger.Default.Send(JsonSerializer.Deserialize<AccountRemovedMessage>(messageJson));
                    break;
                case nameof(AccountUpdatedMessage):
                    WeakReferenceMessenger.Default.Send(JsonSerializer.Deserialize<AccountUpdatedMessage>(messageJson));
                    break;
                case nameof(DraftCreated):
                    WeakReferenceMessenger.Default.Send(JsonSerializer.Deserialize<DraftCreated>(messageJson));
                    break;
                case nameof(DraftFailed):
                    WeakReferenceMessenger.Default.Send(JsonSerializer.Deserialize<DraftFailed>(messageJson));
                    break;
                case nameof(DraftMapped):
                    WeakReferenceMessenger.Default.Send(JsonSerializer.Deserialize<DraftMapped>(messageJson));
                    break;
                case nameof(FolderRenamed):
                    WeakReferenceMessenger.Default.Send(JsonSerializer.Deserialize<FolderRenamed>(messageJson));
                    break;
                case nameof(FolderSynchronizationEnabled):
                    WeakReferenceMessenger.Default.Send(JsonSerializer.Deserialize<FolderSynchronizationEnabled>(messageJson));
                    break;
                case nameof(MergedInboxRenamed):
                    WeakReferenceMessenger.Default.Send(JsonSerializer.Deserialize<MergedInboxRenamed>(messageJson));
                    break;
                case nameof(AccountSynchronizationCompleted):
                    WeakReferenceMessenger.Default.Send(JsonSerializer.Deserialize<AccountSynchronizationCompleted>(messageJson));
                    break;
                default:
                    throw new Exception("Invalid data type name passed to client.");
            }
        }

        private void ServerDisconnected(AppServiceConnection sender, AppServiceClosedEventArgs args)
        {
            // TODO: Handle server disconnection.
        }

        public void DisposeConnection()
        {
            if (Connection == null) return;
        }

        public async Task QueueRequestAsync(IRequestBase request, Guid accountId)
        {
            var queuePackage = new ServerRequestPackage(accountId, request);

            // IRequestBase is not a concrete type, so we need to use a custom type resolver.
            // System.Text.Json must know the concrete type to serialize the object.

            var serialized = JsonSerializer.Serialize(queuePackage, _serverJsonServerSerializer);

            var response = await Connection.SendMessageAsync(new ValueSet
            {
                { MessageConstants.MessageTypeKey, (int)MessageType.ServerMessage },
                { MessageConstants.MessageDataKey, serialized },
                { MessageConstants.MessageDataTypeKey, nameof(ServerRequestPackage) },
                { MessageConstants.MessageDataRequestAccountIdKey, accountId }
            });

            if (response.Status != AppServiceResponseStatus.Success)
                throw new WinoServerException(new Exception($"Failed to queue request to server. Server response was: {response.Status}"));
        }

        public async Task<TResponse> GetResponseAsync<TResponse, TRequestType>(TRequestType message) where TRequestType : IClientMessage
        {
            // TODO: Handle exceptions and disconnections.

            var serialized = JsonSerializer.Serialize(message, _serverJsonServerSerializer);

            var response = await Connection.SendMessageAsync(new ValueSet
            {
                { MessageConstants.MessageTypeKey, (int)MessageType.ServerMessage },
                { MessageConstants.MessageDataKey, serialized },
                { MessageConstants.MessageDataTypeKey, message.GetType().Name }
            });

            if (response.Status == AppServiceResponseStatus.Success)
            {
                if (response.Message.TryGetValue(MessageConstants.MessageDataKey, out object messageDataObject) && messageDataObject is string messageJson)
                {
                    return JsonSerializer.Deserialize<TResponse>(messageJson);
                }
            }

            return default;
        }

    }
}
