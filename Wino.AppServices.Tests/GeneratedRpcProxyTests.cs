using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using FluentAssertions;
using Wino.AppServices.Contracts;
using Wino.AppServices.Contracts.Generated;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.MailItem;
using Wino.Messaging.Server;
using Wino.Messaging.UI;

namespace Wino.AppServices.Tests;

public sealed class GeneratedRpcProxyTests
{
    [Fact]
    public void GeneratorCoversAllBackendInterfaces()
    {
        var contractAssembly = typeof(CompanionBackendControlRemoteProxy).Assembly;
        var proxies = contractAssembly.GetTypes()
            .Where(type => type.Namespace == "Wino.AppServices.Contracts.Generated" && type.Name.EndsWith("RemoteProxy", StringComparison.Ordinal))
            .ToList();

        proxies.Should().HaveCount(22);
        proxies.Should().OnlyContain(proxy => proxy.GetInterfaces().Length == 1);
    }

    [Fact]
    public void ServerCommandsUseOnlyTheUiToCompanionDirection()
    {
        var command = new NewMailSynchronizationRequested(new MailSynchronizationOptions());
        WinoRpcEventRegistry.TrySerializeCompanionToUi(command, out _, out _).Should().BeFalse();
        WinoRpcEventRegistry.TrySerializeUiToCompanion(command, out var messageId, out var payload).Should().BeTrue();
        messageId.Should().Be("server.NewMailSynchronizationRequested.v1");
        payload.Should().NotBeEmpty();
    }

    [Fact]
    public void UnlistedUiMessagesAreNotSentAcrossProcesses()
    {
        var localOnlyMessage = new ThumbnailAdded("local@example.com");

        WinoRpcEventRegistry.TrySerializeCompanionToUi(localOnlyMessage, out _, out _).Should().BeFalse();
        WinoRpcEventRegistry.TrySerializeUiToCompanion(localOnlyMessage, out _, out _).Should().BeFalse();
    }

    [Fact]
    public async Task ProxyUsesStableMethodIdsWithoutMutationJournaling()
    {
        var client = new RecordingClient();
        var proxy = new CompanionBackendControlRemoteProxy(client);

        await proxy.GetVersionAsync();
        await proxy.HasAccountsAsync(CancellationToken.None);
        await proxy.SynchronizeAllAsync(CancellationToken.None);
        await proxy.FlushAsync(CancellationToken.None);

        client.Calls.Should().Equal(
            "ICompanionBackendControl.GetVersionAsync()#v1",
            "ICompanionBackendControl.HasAccountsAsync()#v1",
            "ICompanionBackendControl.SynchronizeAllAsync()#v1",
            "ICompanionBackendControl.FlushAsync()#v1");
        client.Calls.Should().OnlyContain(call => !string.IsNullOrWhiteSpace(call));
    }

    [Fact]
    public async Task NotificationDraftCreationIsSentOnce()
    {
        var client = new RecordingClient();
        var proxy = new MailServiceRemoteProxy(client);

        await proxy.CreateNotificationDraftAsync(Guid.NewGuid(), MailOperation.Reply);

        client.Calls.Should().ContainSingle();
        client.Calls[0].Should().StartWith("IMailService.CreateNotificationDraftAsync(");
    }

    private sealed class RecordingClient : IWinoRpcClient
    {
        public List<string> Calls { get; } = [];

        public Task<TResponse> InvokeAsync<TRequest, TResponse>(string methodId, TRequest request, JsonTypeInfo<TRequest> requestTypeInfo, JsonTypeInfo<TResponse> responseTypeInfo, CancellationToken cancellationToken)
        {
            Calls.Add(methodId);
            object? response = typeof(TResponse) == typeof(string)
                ? "1.0.0.0"
                : typeof(TResponse) == typeof(bool)
                    ? true
                    : typeof(TResponse) == typeof(DraftPreparationRequest)
                        ? null
                        : Activator.CreateInstance<TResponse>();
            return Task.FromResult((TResponse)response!);
        }

        public Task InvokeAsync<TRequest>(string methodId, TRequest request, JsonTypeInfo<TRequest> requestTypeInfo, CancellationToken cancellationToken)
        {
            Calls.Add(methodId);
            return Task.CompletedTask;
        }
    }
}
