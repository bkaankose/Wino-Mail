#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Agent.Runtime.Ports;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Voice;
using Wino.Mail.Api.Contracts.Assistant;

namespace Wino.Services.Assistant;

/// <summary>
/// The runtime's classifier transport over the app's authenticated client: a typed request opens the interpret
/// socket, sends the words in a text frame and waits for the reading; composed content goes to the compose endpoint.
/// A failure is an <see cref="AssistantReadException"/> with the API's or the socket's error code.
/// </summary>
public sealed class AssistantReadingClient(IAssistantCommandService commands, IWinoAccountApiClient apiClient) : IReadingClient
{
    public async Task<AssistantReading> ReadAsync(AssistantReadRequest request, CancellationToken ct)
    {
        var start = await commands.StartAsync(request, ct).ConfigureAwait(false);
        if (start.Session is not { } session)
            throw new AssistantReadException(start.ErrorCode ?? WinoAccountClientErrorCodes.ServiceUnavailable);

        await using (session.ConfigureAwait(false))
        {
            session.SendText(request.Text ?? string.Empty);
            await foreach (var frame in session.Frames.ReadAllAsync(ct).ConfigureAwait(false))
            {
                switch (frame.Type)
                {
                    case AssistantFrameTypes.Reading when frame.Reading is { } reading:
                        LastMetrics = frame.Metrics;
                        return reading;
                    case AssistantFrameTypes.Error:
                        throw new AssistantReadException(frame.Code ?? VoiceClientErrorCodes.ConnectionLost, frame.Message);
                }
            }
        }

        throw new AssistantReadException(VoiceClientErrorCodes.ConnectionLost);
    }

    /// <summary>The metrics of the last reading (quota left), for the bubble's notices.</summary>
    public AssistantMetrics? LastMetrics { get; private set; }

    public async Task<AssistantComposeResult> ComposeAsync(AssistantComposeRequest request, CancellationToken ct)
    {
        var envelope = await apiClient.ComposeAssistantAsync(request, ct).ConfigureAwait(false);
        if (envelope.IsSuccess && envelope.Result is { } result) return result;
        throw new AssistantReadException(envelope.ErrorCode ?? WinoAccountClientErrorCodes.ServiceUnavailable, envelope.Message);
    }
}

/// <summary>The read or compose call failed; <see cref="Code"/> is an API error code, a socket error code or a <see cref="WinoAccountClientErrorCodes"/> value.</summary>
public sealed class AssistantReadException(string code, string? message = null) : Exception(message ?? code)
{
    public string Code { get; } = code;
}
