using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using SQLite;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Ipc.Contracts;
using Wino.Ipc.Contracts.Generated;
using Wino.Ipc.Protocol;
using Wino.Ipc.Transport;
using Wino.Messaging.UI;
using Wino.Services;
using Xunit;

namespace Wino.Ipc.Tests;

/// <summary>
/// End-to-end integration over a real named pipe (no MSIX): real database service classes
/// hosted in-proc behind the generated dispatcher, generated remote proxies on the client
/// side — exactly the wiring the UI and the background companion use.
/// </summary>
public class NamedPipeIntegrationTests : IAsyncLifetime
{
    private const int ProtocolVersion = 1;

    private string _pipeName = null!;
    private string _databasePath = null!;
    private TempDatabaseService _databaseService = null!;
    private ThumbnailCacheService _thumbnailCacheService = null!;
    private NamedPipeRpcServerHost _serverHost = null!;

    public async Task InitializeAsync()
    {
        _pipeName = $"wino-ipc-tests-{Guid.NewGuid():N}";
        _databasePath = Path.Combine(Path.GetTempPath(), $"wino-ipc-tests-{Guid.NewGuid():N}.db");

        _databaseService = new TempDatabaseService(_databasePath);
        await _databaseService.InitializeAsync();

        _thumbnailCacheService = new ThumbnailCacheService(_databaseService);

        _serverHost = CreateHost(_pipeName, _thumbnailCacheService);
        await _serverHost.Start().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static NamedPipeRpcServerHost CreateHost(string pipeName, IThumbnailCacheService service)
        => new(pipeName,
               new ThumbnailCacheServiceDispatcher(service),
               new RpcServerConnectionOptions
               {
                   ProtocolVersion = ProtocolVersion,
                   AppVersion = "1.0.0-tests",
                   ExceptionMapper = WinoRpcDomainExceptions.ToErrorEnvelope,
                   OperationDeduplicator = new RpcOperationDeduplicator(),
               });

    public async Task DisposeAsync()
    {
        await _serverHost.DisposeAsync();
        await _databaseService.DisposeAsync();

        try
        {
            File.Delete(_databasePath);
        }
        catch
        {
        }
    }

    private async Task<RpcClient> ConnectAsync()
        => await ConnectAsync(_pipeName);

    private async Task<RpcClient> ConnectAsync(string pipeName)
    {
        var stream = await NamedPipeTransport.ConnectAsync(pipeName, TimeSpan.FromSeconds(5));
        var client = new RpcClient(stream, WinoRpcDomainExceptions.ToException);

        var handshake = await client.HandshakeAsync(new HandshakeRequest(ProtocolVersion, "1.0.0-tests", "integration-tests"));
        handshake.Accepted.Should().BeTrue();

        return client;
    }

    [Fact]
    public async Task Start_SignalsReadiness_WhenListenerIsPosted()
    {
        var pipeName = $"wino-ipc-tests-{Guid.NewGuid():N}";
        await using var host = CreateHost(pipeName, _thumbnailCacheService);

        await host.Start().WaitAsync(TimeSpan.FromSeconds(5));

        await using var client = await ConnectAsync(pipeName);
        client.IsConnected.Should().BeTrue();
    }

    [Fact]
    public async Task ImmediateClientConnection_AfterReadiness_DoesNotNeedStartupDelay()
    {
        var pipeName = $"wino-ipc-tests-{Guid.NewGuid():N}";
        await using var host = CreateHost(pipeName, _thumbnailCacheService);

        await host.Start().WaitAsync(TimeSpan.FromSeconds(5));
        await using var client = await ConnectAsync(pipeName);

        var proxy = new ThumbnailCacheServiceRemoteProxy(client);
        var result = await proxy.GetThumbnailAsync("startup@wino.mail");

        result.Should().BeNull();
    }

    [Fact]
    public async Task ConcurrentStartupClients_AfterReadiness_AreServed()
    {
        var pipeName = $"wino-ipc-tests-{Guid.NewGuid():N}";
        await using var host = CreateHost(pipeName, _thumbnailCacheService);

        await host.Start().WaitAsync(TimeSpan.FromSeconds(5));

        var clients = await Task.WhenAll(Enumerable
            .Range(0, NamedPipeTransport.MaxServerInstances)
            .Select(_ => ConnectAsync(pipeName)));

        try
        {
            clients.Should().AllSatisfy(client => client.IsConnected.Should().BeTrue());
        }
        finally
        {
            foreach (var client in clients)
            {
                await client.DisposeAsync();
            }
        }
    }

    [Fact]
    public void CompanionProcessMutexName_IsDeterministic()
    {
        CompanionProcessNaming.SingleInstanceMutexName.Should().Be(@"Local\WinoBackgroundServiceRunning");
    }

    [Fact]
    public async Task GeneratedProxy_CrudRoundTrip_AgainstRealDatabase()
    {
        await using var client = await ConnectAsync();
        var proxy = new ThumbnailCacheServiceRemoteProxy(client);

        var thumbnail = new Thumbnail
        {
            Domain = "test@wino.mail",
            GravatarFileName = "gravatar.jpg",
            FaviconFileName = "favicon.png",
            LastUpdated = DateTime.UtcNow,
        };

        await proxy.SaveThumbnailAsync(thumbnail);

        var loaded = await proxy.GetThumbnailAsync("test@wino.mail");
        loaded.Should().NotBeNull();
        loaded.GravatarFileName.Should().Be("gravatar.jpg");
        loaded.FaviconFileName.Should().Be("favicon.png");

        await proxy.DeleteThumbnailAsync("test@wino.mail");

        var afterDelete = await proxy.GetThumbnailAsync("test@wino.mail");
        afterDelete.Should().BeNull();
    }

    [Fact]
    public async Task ForwardedUIMessage_RoundTrips_ThroughEventRegistryAndMessenger()
    {
        await using var client = await ConnectAsync();

        var receivedMessage = new TaskCompletionSource<MailAddedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var messenger = new WeakReferenceMessenger();
        messenger.Register<MailAddedMessage>(this, (_, message) => receivedMessage.TrySetResult(message));

        client.EventReceived += (typeName, payload) =>
        {
            WinoRpcEventRegistry.TryPublishToMessenger(typeName, payload.Clone(), messenger).Should().BeTrue();
        };

        var mail = new MailCopy
        {
            UniqueId = Guid.NewGuid(),
            Id = "mail-1",
            Subject = "Forwarded subject",
            FromAddress = "sender@wino.mail",
        };

        // Companion side: serialize through the registry, push to all connected clients.
        WinoRpcEventRegistry.TrySerialize(new MailAddedMessage(mail), out var typeName, out var payloadBytes).Should().BeTrue();
        _serverHost.PublishEvent(typeName, payloadBytes);

        var forwarded = await receivedMessage.Task.WaitAsync(TimeSpan.FromSeconds(5));
        forwarded.AddedMail.UniqueId.Should().Be(mail.UniqueId);
        forwarded.AddedMail.Subject.Should().Be("Forwarded subject");
    }

    [Fact]
    public async Task KillingServerMidCall_FailsFast_AndReconnectToNewHostWorks()
    {
        var client = await ConnectAsync();
        var proxy = new ThumbnailCacheServiceRemoteProxy(client);

        // Server goes away (companion crash).
        await _serverHost.DisposeAsync();

        var call = () => proxy.GetThumbnailAsync("whatever@wino.mail");
        await call.Should().ThrowAsync<WinoRpcConnectionLostException>();
        await client.DisposeAsync();

        // Companion is relaunched on the same pipe; a fresh connection works again.
        _serverHost = CreateHost(_pipeName, _thumbnailCacheService);
        await _serverHost.Start().WaitAsync(TimeSpan.FromSeconds(5));

        await using var recoveredClient = await ConnectAsync();
        var recoveredProxy = new ThumbnailCacheServiceRemoteProxy(recoveredClient);

        var result = await recoveredProxy.GetThumbnailAsync("whatever@wino.mail");
        result.Should().BeNull();
    }

    [Fact]
    public async Task MultipleClients_AreServedConcurrently()
    {
        await using var firstClient = await ConnectAsync();
        await using var secondClient = await ConnectAsync();

        var firstProxy = new ThumbnailCacheServiceRemoteProxy(firstClient);
        var secondProxy = new ThumbnailCacheServiceRemoteProxy(secondClient);

        await firstProxy.SaveThumbnailAsync(new Thumbnail { Domain = "a@wino.mail", LastUpdated = DateTime.UtcNow });
        await secondProxy.SaveThumbnailAsync(new Thumbnail { Domain = "b@wino.mail", LastUpdated = DateTime.UtcNow });

        (await secondProxy.GetThumbnailAsync("a@wino.mail")).Should().NotBeNull();
        (await firstProxy.GetThumbnailAsync("b@wino.mail")).Should().NotBeNull();
    }

    private sealed class TempDatabaseService : IDatabaseService, IAsyncDisposable
    {
        static TempDatabaseService()
        {
            // sqlite-net-base (Domain's flavor of SQLite-net.dll) does not auto-initialize
            // the native provider the way sqlite-net-pcl does.
            SQLitePCL.Batteries_V2.Init();
        }

        public TempDatabaseService(string databasePath)
        {
            Connection = new SQLiteAsyncConnection(databasePath);
        }

        public SQLiteAsyncConnection Connection { get; }

        public async Task InitializeAsync()
        {
            await Connection.CreateTableAsync<Thumbnail>();
            await Connection.CreateTableAsync<MailCopy>();
        }

        public async ValueTask DisposeAsync()
        {
            await Connection.CloseAsync();
        }
    }
}
