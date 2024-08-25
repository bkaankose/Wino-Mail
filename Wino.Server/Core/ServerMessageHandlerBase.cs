using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.AppService;
using Windows.Foundation.Collections;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Server;
using Wino.Messaging;

namespace Wino.Server.Core
{
    public abstract class ServerMessageHandlerBase
    {
        public string HandlingRequestType { get; }

        public abstract Task ExecuteAsync(IClientMessage message, AppServiceRequest request = null, CancellationToken cancellationToken = default);
    }

    public abstract class ServerMessageHandler<TClientMessage, TResponse> : ServerMessageHandlerBase where TClientMessage : IClientMessage
    {
        /// <summary>
        /// Response to return when server encounters and exception while executing code.
        /// </summary>
        /// <param name="ex">Exception that target threw.</param>
        /// <returns>Default response on failure object.</returns>
        public abstract WinoServerResponse<TResponse> FailureDefaultResponse(Exception ex);


        /// <summary>
        /// Safely executes the handler code and returns the response.
        /// This call will never crash the server. Exceptions encountered will be handled and returned as response.
        /// </summary>
        /// <param name="message">IClientMessage that client asked the response for from the server.</param>
        /// <param name="request">optional AppServiceRequest to return response for.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Response object that server executes for the given method.</returns>
        public override async Task ExecuteAsync(IClientMessage message, AppServiceRequest request = null, CancellationToken cancellationToken = default)
        {
            WinoServerResponse<TResponse> response = default;

            try
            {
                response = await HandleAsync((TClientMessage)message, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                response = FailureDefaultResponse(ex);
            }
            finally
            {
                // No need to send response if request is null.
                // Handler might've been called directly from the server itself.
                if (request != null)
                {
                    var valueSet = new ValueSet()
                    {
                        { MessageConstants.MessageDataKey, JsonSerializer.Serialize(response) }
                    };

                    await request.SendResponseAsync(valueSet);
                }
            }
        }

        /// <summary>
        /// Code that will be executed directly on the server.
        /// All handlers must implement this method.
        /// Response is wrapped with WinoServerResponse.
        /// </summary>
        /// <param name="message">IClientMessage that client asked the response for from the server.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        protected abstract Task<WinoServerResponse<TResponse>> HandleAsync(TClientMessage message, CancellationToken cancellationToken = default);
    }
}
