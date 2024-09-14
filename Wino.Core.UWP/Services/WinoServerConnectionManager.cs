using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Nito.AsyncEx;
using Serilog;
using Windows.ApplicationModel;
using Windows.ApplicationModel.AppService;
using Windows.Foundation.Collections;
using Windows.Foundation.Metadata;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Requests;
using Wino.Core.Domain.Models.Server;
using Wino.Core.Integration.Json;
using Wino.Messaging;
using Wino.Messaging.Client.Connection;
using Wino.Messaging.Enums;
using Wino.Messaging.UI;

namespace Wino.Core.UWP.Services
{
    public class WinoServerConnectionManager :
        IWinoServerConnectionManager<AppServiceConnection>,
        IRecipient<WinoServerConnectionEstablished>
    {
        private const int ServerConnectionTimeoutMs = 10000;

        public event EventHandler<WinoServerConnectionStatus> StatusChanged;

        public TaskCompletionSource<bool> ConnectingHandle { get; private set; }

        private ILogger Logger => Logger.ForContext<WinoServerConnectionManager>();

        private WinoServerConnectionStatus status;

        public WinoServerConnectionStatus Status
        {
            get { return status; }
            private set
            {
                Log.Information("Server connection status changed to {Status}.", value);
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

        private readonly JsonSerializerOptions _jsonSerializerOptions = new()
        {
            TypeInfoResolver = new ServerRequestTypeInfoResolver()
        };

        public WinoServerConnectionManager()
        {
            WeakReferenceMessenger.Default.Register(this);
        }

        public async Task<bool> ConnectAsync()
        {
            if (Status == WinoServerConnectionStatus.Connected)
            {
                Log.Information("Server is already connected.");
                return true;
            }

            if (Status == WinoServerConnectionStatus.Connecting)
            {
                // A connection is already being established at the moment.
                // No need to run another connection establishment process.
                // Await the connecting handler if possible.

                if (ConnectingHandle != null)
                {
                    return await ConnectingHandle.Task;
                }
            }

            if (ApiInformation.IsApiContractPresent("Windows.ApplicationModel.FullTrustAppContract", 1, 0))
            {
                try
                {
                    ConnectingHandle = new TaskCompletionSource<bool>();

                    Status = WinoServerConnectionStatus.Connecting;

                    var connectionCancellationToken = new CancellationTokenSource(TimeSpan.FromMilliseconds(ServerConnectionTimeoutMs));

                    await FullTrustProcessLauncher.LaunchFullTrustProcessForCurrentAppAsync();

                    // Connection establishment handler is in App.xaml.cs OnBackgroundActivated.
                    // Once the connection is established, the handler will set the Connection property
                    // and WinoServerConnectionEstablished will be fired by the messenger.

                    await ConnectingHandle.Task.WaitAsync(connectionCancellationToken.Token);

                    Log.Information("Server connection established successfully.");
                }
                catch (OperationCanceledException canceledException)
                {
                    Log.Error(canceledException, $"Server process did not start in {ServerConnectionTimeoutMs} ms. Operation is canceled.");

                    ConnectingHandle?.TrySetException(canceledException);

                    Status = WinoServerConnectionStatus.Failed;
                    return false;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to connect to the server.");

                    ConnectingHandle?.TrySetException(ex);

                    Status = WinoServerConnectionStatus.Failed;
                    return false;
                }

                return true;
            }
            else
            {
                Log.Information("FullTrustAppContract is not present in the system. Server connection is not possible.");
            }

            return false;
        }

        public async Task InitializeAsync()
        {
            var isConnectionSuccessfull = await ConnectAsync();

            if (isConnectionSuccessfull)
            {
                Log.Information("ServerConnectionManager initialized successfully.");
            }
            else
            {
                Log.Error("ServerConnectionManager initialization failed.");
            }
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
                case nameof(RefreshUnreadCountsMessage):
                    WeakReferenceMessenger.Default.Send(JsonSerializer.Deserialize<RefreshUnreadCountsMessage>(messageJson));
                    break;
                case nameof(AccountSynchronizerStateChanged):
                    WeakReferenceMessenger.Default.Send(JsonSerializer.Deserialize<AccountSynchronizerStateChanged>(messageJson));
                    break;
                case nameof(AccountSynchronizationProgressUpdatedMessage):
                    WeakReferenceMessenger.Default.Send(JsonSerializer.Deserialize<AccountSynchronizationProgressUpdatedMessage>(messageJson));
                    break;
                case nameof(AccountFolderConfigurationUpdated):
                    WeakReferenceMessenger.Default.Send(JsonSerializer.Deserialize<AccountFolderConfigurationUpdated>(messageJson));
                    break;
                case nameof(CopyAuthURLRequested):
                    WeakReferenceMessenger.Default.Send(JsonSerializer.Deserialize<CopyAuthURLRequested>(messageJson));
                    break;
                default:
                    throw new Exception("Invalid data type name passed to client.");
            }
        }

        private void ServerDisconnected(AppServiceConnection sender, AppServiceClosedEventArgs args)
        {
            Log.Information("Server disconnected.");
        }

        public async Task QueueRequestAsync(IRequestBase request, Guid accountId)
        {
            var queuePackage = new ServerRequestPackage(accountId, request);

            var queueResponse = await GetResponseInternalAsync<bool, ServerRequestPackage>(queuePackage, new Dictionary<string, object>()
            {
                { MessageConstants.MessageDataRequestAccountIdKey, accountId }
            });

            queueResponse.ThrowIfFailed();
        }

        public Task<WinoServerResponse<TResponse>> GetResponseAsync<TResponse, TRequestType>(TRequestType message, CancellationToken cancellationToken = default) where TRequestType : IClientMessage
            => GetResponseInternalAsync<TResponse, TRequestType>(message, cancellationToken: cancellationToken);

        private async Task<WinoServerResponse<TResponse>> GetResponseInternalAsync<TResponse, TRequestType>(TRequestType message,
                                                                                                            Dictionary<string, object> parameters = null,
                                                                                                            CancellationToken cancellationToken = default)
        {
            if (Status != WinoServerConnectionStatus.Connected)
                await ConnectAsync();

            string serializedMessage = string.Empty;

            try
            {
                serializedMessage = JsonSerializer.Serialize(message, _jsonSerializerOptions);
            }
            catch (Exception serializationException)
            {
                Logger.Error(serializationException, $"Failed to serialize client message for sending.");
                return WinoServerResponse<TResponse>.CreateErrorResponse($"Failed to serialize message.\n{serializationException.Message}");
            }

            AppServiceResponse response = null;

            try
            {
                var valueSet = new ValueSet
                {
                    { MessageConstants.MessageTypeKey, (int)MessageType.ServerMessage },
                    { MessageConstants.MessageDataKey, serializedMessage },
                    { MessageConstants.MessageDataTypeKey, message.GetType().Name }
                };

                // Add additional parameters into ValueSet
                if (parameters != null)
                {
                    foreach (var item in parameters)
                    {
                        valueSet.Add(item.Key, item.Value);
                    }
                }

                response = await Connection.SendMessageAsync(valueSet).AsTask(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return WinoServerResponse<TResponse>.CreateErrorResponse($"Request is canceled by client.");
            }
            catch (Exception serverSendException)
            {
                Logger.Error(serverSendException, $"Failed to send message to server.");
                return WinoServerResponse<TResponse>.CreateErrorResponse($"Failed to send message to server.\n{serverSendException.Message}");
            }

            // It should be always Success.
            if (response.Status != AppServiceResponseStatus.Success)
                return WinoServerResponse<TResponse>.CreateErrorResponse($"Wino Server responded with '{response.Status}' status to message delivery.");

            // All responses must contain a message data.
            if (!(response.Message.TryGetValue(MessageConstants.MessageDataKey, out object messageDataObject) && messageDataObject is string messageJson))
                return WinoServerResponse<TResponse>.CreateErrorResponse("Server response did not contain message data.");

            // Try deserialize the message data.
            try
            {
                return JsonSerializer.Deserialize<WinoServerResponse<TResponse>>(messageJson);
            }
            catch (Exception jsonDeserializationError)
            {
                Logger.Error(jsonDeserializationError, $"Failed to deserialize server response message data.");
                return WinoServerResponse<TResponse>.CreateErrorResponse($"Failed to deserialize Wino server response message data.\n{jsonDeserializationError.Message}");
            }
        }

        public void Receive(WinoServerConnectionEstablished message)
            => ConnectingHandle?.TrySetResult(true);
    }
}
