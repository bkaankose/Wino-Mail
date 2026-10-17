#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Models.PushRelay;

namespace Wino.PushRelay.Socket.Transport;

/// <summary>
/// The SignalR client against WinoRelay's hub: WebSockets only, JSON protocol with the source-generated context,
/// bearer token and <c>X-Device-Id</c> on the negotiate and the upgrade. A refused negotiate keeps the relay's
/// problem document in <see cref="LastProblem"/>, which SignalR itself drops.
/// </summary>
public sealed class SignalRRelayHubConnection : IRelayHubConnection
{
    private readonly HubConnection _connection;
    private readonly ProblemCapturingHandler _problems = new();

    public SignalRRelayHubConnection(RelayHubConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _connection = new HubConnectionBuilder()
            .WithUrl(settings.HubUri, options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.AccessTokenProvider = async () => await settings.AccessTokenProvider(CancellationToken.None).ConfigureAwait(false);
                options.Headers[RelaySocketProtocol.DeviceIdHeaderName] = settings.DeviceId.ToString("D");
                options.HttpMessageHandlerFactory = inner =>
                {
                    _problems.InnerHandler = inner;
                    return _problems;
                };
            })
            .AddJsonProtocol(options => options.PayloadSerializerOptions.TypeInfoResolver = RelaySocketJsonContext.Default)
            .Build();

        // The relay pings every 15 s and gives the client 60 s; mirror that so a dead tunnel is noticed in time.
        _connection.KeepAliveInterval = TimeSpan.FromSeconds(15);
        _connection.ServerTimeout = TimeSpan.FromSeconds(45);
        _connection.Closed += error => Closed?.Invoke(error) ?? Task.CompletedTask;
    }

    public PushRelayProblem? LastProblem => _problems.LastProblem;

    public event Func<Exception?, Task>? Closed;

    public void OnHello(Func<RelaySocketHello, Task> handler) => _connection.On(RelaySocketProtocol.HelloMethod, handler);
    public void OnEvent(Func<Dictionary<string, string>, Task> handler) => _connection.On(RelaySocketProtocol.EventMethod, handler);
    public void OnBye(Func<string, Task> handler) => _connection.On(RelaySocketProtocol.ByeMethod, handler);

    public Task StartAsync(CancellationToken cancellationToken) => _connection.StartAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => _connection.StopAsync(cancellationToken);

    public Task<int> AckAsync(string[] eventIds, CancellationToken cancellationToken)
        => _connection.InvokeAsync<int>(RelaySocketProtocol.AckMethod, eventIds, cancellationToken);

    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    /// <summary>Keeps the problem document of the last refused negotiate; everything else passes through untouched.</summary>
    private sealed class ProblemCapturingHandler : DelegatingHandler
    {
        public PushRelayProblem? LastProblem { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                LastProblem = await RelaySocketProblemReader.ReadAsync(response, cancellationToken).ConfigureAwait(false);
            return response;
        }
    }
}

public sealed class SignalRRelayHubConnectionFactory : IRelayHubConnectionFactory
{
    public IRelayHubConnection Create(RelayHubConnectionSettings settings) => new SignalRRelayHubConnection(settings);
}
