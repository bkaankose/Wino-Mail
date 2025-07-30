using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Windows.ApplicationModel;
using Windows.ApplicationModel.AppService;
using Windows.Foundation.Collections;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Requests;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Integration.Json;
using Wino.Messaging;
using Wino.Messaging.Enums;
using Wino.Messaging.Server;
using Wino.Messaging.UI;
using Wino.Server.MessageHandlers;
using Wino.Services;

namespace Wino.Server;

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
    IRecipient<MergedInboxRenamed>,
    IRecipient<AccountSynchronizationCompleted>,
    IRecipient<AccountSynchronizerStateChanged>,
    IRecipient<RefreshUnreadCountsMessage>,
    IRecipient<ServerTerminationModeChanged>,
    IRecipient<AccountSynchronizationProgressUpdatedMessage>,
    IRecipient<AccountFolderConfigurationUpdated>,
    IRecipient<CopyAuthURLRequested>,
    IRecipient<NewMailSynchronizationRequested>,
    IRecipient<OnlineSearchRequested>,
    IRecipient<AccountCacheResetMessage>
{
    private const double MinimumSynchronizationIntervalMinutes = 1;

    private readonly System.Timers.Timer _timer = new System.Timers.Timer();
    private static object connectionLock = new object();

    private AppServiceConnection connection = null;

    private readonly IServerMessageHandlerFactory _serverMessageHandlerFactory;
    private readonly IAccountService _accountService;
    private readonly IPreferencesService _preferencesService;
    private readonly JsonSerializerOptions _jsonSerializerOptions = new JsonSerializerOptions
    {
        TypeInfoResolver = new ServerRequestTypeInfoResolver()
    };

    public ServerContext(IDatabaseService databaseService,
                         IApplicationConfiguration applicationFolderConfiguration,
                         ISynchronizerFactory synchronizerFactory,
                         IServerMessageHandlerFactory serverMessageHandlerFactory,
                         IAccountService accountService,
                         IPreferencesService preferencesService)
    {
        _preferencesService = preferencesService;

        _timer.Elapsed += SynchronizationTimerTriggered;
        _preferencesService.PropertyChanged += PreferencesUpdated;

        _serverMessageHandlerFactory = serverMessageHandlerFactory;
        _accountService = accountService;

        WeakReferenceMessenger.Default.RegisterAll(this);

        // Setup timer for synchronization.
        RestartSynchronizationTimer();
    }

    private void PreferencesUpdated(object sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IPreferencesService.EmailSyncIntervalMinutes))
            RestartSynchronizationTimer();
    }

    private void RestartSynchronizationTimer()
    {
        _timer.Stop();

        // Ensure that the interval is at least 1 minute.
        _timer.Interval = 1000 * 60 * Math.Max(MinimumSynchronizationIntervalMinutes, _preferencesService.EmailSyncIntervalMinutes);
        _timer.Start();
    }

    private async void SynchronizationTimerTriggered(object sender, System.Timers.ElapsedEventArgs e)
    {
        if (Debugger.IsAttached) return;

        // Send sync request for all accounts.

        var accounts = await _accountService.GetAccountsAsync();

        foreach (var account in accounts)
        {
            var options = new MailSynchronizationOptions
            {
                AccountId = account.Id,
                Type = MailSynchronizationType.InboxOnly,
            };

            var request = new NewMailSynchronizationRequested(options, SynchronizationSource.Server);

            await ExecuteServerMessageSafeAsync(null, request);
        }
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

    public async void Receive(AccountSynchronizationCompleted message) => await SendMessageAsync(MessageType.UIMessage, message);

    public async void Receive(RefreshUnreadCountsMessage message) => await SendMessageAsync(MessageType.UIMessage, message);

    public async void Receive(AccountSynchronizerStateChanged message) => await SendMessageAsync(MessageType.UIMessage, message);

    public async void Receive(AccountSynchronizationProgressUpdatedMessage message) => await SendMessageAsync(MessageType.UIMessage, message);

    public async void Receive(AccountFolderConfigurationUpdated message) => await SendMessageAsync(MessageType.UIMessage, message);

    public async void Receive(CopyAuthURLRequested message) => await SendMessageAsync(MessageType.UIMessage, message);

    public async void Receive(NewMailSynchronizationRequested message) => await SendMessageAsync(MessageType.UIMessage, message);

    public async void Receive(OnlineSearchRequested message) => await SendMessageAsync(MessageType.UIMessage, message);

    public async void Receive(AccountCacheResetMessage message) => await SendMessageAsync(MessageType.UIMessage, message);

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
            Log.Error("Opening server connection failed. Status: {status}", status);

            DisposeConnection();
        }
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

        if (message is not IUIMessage serverMessage)
            throw new ArgumentException("Server message must be a type of IUIMessage");

        string json = JsonSerializer.Serialize(message);

        var set = new ValueSet
        {
            { MessageConstants.MessageTypeKey, (int)messageType },
            { MessageConstants.MessageDataKey, json },
            { MessageConstants.MessageDataTypeKey, message.GetType().Name }
        };

        try
        {
            await connection.SendMessageAsync(set);
        }
        catch (InvalidOperationException)
        {
            // Connection might've been disposed during the SendMessageAsync call.
            // This is a safe way to handle the exception.
            // We don't lock the connection since this request may take sometime to complete.
        }
        catch (Exception exception)
        {
            Log.Error(exception, "SendMessageAsync threw an exception");
        }
    }

    private void OnConnectionClosed(AppServiceConnection sender, AppServiceClosedEventArgs args)
    {
        // UWP app might've been terminated or suspended.
        // At this point, we must keep active synchronizations going, but connection is lost.
        // As long as this process is alive, database will be kept updated, but no messages will be sent.

        DisposeConnection();
    }

    private async void OnWinRTMessageReceived(AppServiceConnection sender, AppServiceRequestReceivedEventArgs args)
    {
        if (args.Request.Message.TryGetValue(MessageConstants.MessageTypeKey, out object messageTypeObject) && messageTypeObject is int messageTypeInt)
        {
            var messageType = (MessageType)messageTypeInt;

            if (args.Request.Message.TryGetValue(MessageConstants.MessageDataKey, out object messageDataObject) && messageDataObject is string messageJson)
            {
                if (!args.Request.Message.TryGetValue(MessageConstants.MessageDataTypeKey, out object dataTypeObject) || dataTypeObject is not string dataTypeName)
                    throw new ArgumentException("Message data type is missing.");

                if (messageType == MessageType.ServerMessage)
                {
                    // Client is awaiting a response from server.
                    // ServerMessage calls are awaited on the server and response is returned back in the args.

                    await HandleServerMessageAsync(messageJson, dataTypeName, args).ConfigureAwait(false);
                }
                else if (messageType == MessageType.UIMessage)
                    throw new Exception("Received UIMessage from UWP. This is not expected.");
            }
        }
    }

    private async Task HandleServerMessageAsync(string messageJson, string typeName, AppServiceRequestReceivedEventArgs args)
    {
        switch (typeName)
        {
            case nameof(NewMailSynchronizationRequested):
                Debug.WriteLine($"New mail synchronization requested.");

                await ExecuteServerMessageSafeAsync(args, JsonSerializer.Deserialize<NewMailSynchronizationRequested>(messageJson, _jsonSerializerOptions));
                break;
            case nameof(NewCalendarSynchronizationRequested):
                Debug.WriteLine($"New calendar synchronization requested.");

                await ExecuteServerMessageSafeAsync(args, JsonSerializer.Deserialize<NewCalendarSynchronizationRequested>(messageJson, _jsonSerializerOptions));
                break;
            case nameof(DownloadMissingMessageRequested):
                Debug.WriteLine($"Download missing message requested.");

                await ExecuteServerMessageSafeAsync(args, JsonSerializer.Deserialize<DownloadMissingMessageRequested>(messageJson, _jsonSerializerOptions));
                break;
            case nameof(ServerRequestPackage):
                var serverPackage = JsonSerializer.Deserialize<ServerRequestPackage>(messageJson, _jsonSerializerOptions);

                Debug.WriteLine(serverPackage);

                await ExecuteServerMessageSafeAsync(args, serverPackage);
                break;
            case nameof(AuthorizationRequested):
                Debug.WriteLine($"Authorization requested.");

                await ExecuteServerMessageSafeAsync(args, JsonSerializer.Deserialize<AuthorizationRequested>(messageJson, _jsonSerializerOptions));
                break;
            case nameof(SynchronizationExistenceCheckRequest):

                await ExecuteServerMessageSafeAsync(args, JsonSerializer.Deserialize<SynchronizationExistenceCheckRequest>(messageJson, _jsonSerializerOptions));
                break;

            case nameof(ServerTerminationModeChanged):
                await ExecuteServerMessageSafeAsync(args, JsonSerializer.Deserialize<ServerTerminationModeChanged>(messageJson, _jsonSerializerOptions));
                break;
            case nameof(ImapConnectivityTestRequested):
                await ExecuteServerMessageSafeAsync(args, JsonSerializer.Deserialize<ImapConnectivityTestRequested>(messageJson, _jsonSerializerOptions));
                break;
            case nameof(TerminateServerRequested):
                await ExecuteServerMessageSafeAsync(args, JsonSerializer.Deserialize<TerminateServerRequested>(messageJson, _jsonSerializerOptions));

                KillServer();
                break;
            case nameof(KillAccountSynchronizerRequested):
                await ExecuteServerMessageSafeAsync(args, JsonSerializer.Deserialize<KillAccountSynchronizerRequested>(messageJson, _jsonSerializerOptions));
                break;
            case nameof(OnlineSearchRequested):
                await ExecuteServerMessageSafeAsync(args, JsonSerializer.Deserialize<OnlineSearchRequested>(messageJson, _jsonSerializerOptions));
                break;
            default:
                Debug.WriteLine($"Missing handler for {typeName} in the server. Check ServerContext.cs - HandleServerMessageAsync.");
                break;
        }
    }

    private void KillServer()
    {
        DisposeConnection();

        Application.Current.Dispatcher.Invoke(() =>
        {
            Application.Current.Shutdown();
        });
    }

    /// <summary>
    /// Executes ServerMessage coming from the UWP.
    /// These requests are awaited and expected to return a response.
    /// </summary>
    /// <param name="args">App service request args.</param>
    /// <param name="message">Message that client sent to server.</param>
    private async Task ExecuteServerMessageSafeAsync(AppServiceRequestReceivedEventArgs args, IClientMessage message)
    {
        AppServiceDeferral deferral = args?.GetDeferral() ?? null;

        try
        {
            var messageName = message.GetType().Name;

            var handler = _serverMessageHandlerFactory.GetHandler(messageName);
            await handler.ExecuteAsync(message, args?.Request ?? null).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ExecuteServerMessageSafeAsync crashed.");
            Debugger.Break();
        }
        finally
        {
            deferral?.Complete();
        }
    }

    public void Receive(ServerTerminationModeChanged message)
    {
        var backgroundMode = message.ServerBackgroundMode;

        bool isServerTrayIconVisible = backgroundMode == ServerBackgroundMode.MinimizedTray || backgroundMode == ServerBackgroundMode.Terminate;

        App.Current.ChangeNotifyIconVisiblity(isServerTrayIconVisible);
    }
}
