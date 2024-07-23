using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.AppService;
using Windows.Foundation.Collections;
using Wino.Core.Domain.Interfaces;
using Wino.Messaging;

namespace Wino.Server.Core
{
    public abstract class ServerMessageHandlerBase
    {
        public string HandlingRequestType { get; }

        public abstract Task ExecuteAsync(IClientMessage message, AppServiceRequest request, CancellationToken cancellationToken = default);
    }

    public abstract class ServerMessageHandler<TClientMessage, TResponse> : ServerMessageHandlerBase where TClientMessage : IClientMessage
    {
        /// <summary>
        /// Response to return when server encounters and exception while executing code.
        /// </summary>
        /// <param name="ex">Exception that target threw.</param>
        /// <returns>Default response on failure object.</returns>
        public abstract TResponse FailureDefaultResponse(Exception ex);

        /// <summary>
        /// Safely executes the handler code and returns the response.
        /// This call will never crash the server. Exceptions encountered will be handled and returned as response.
        /// </summary>
        /// <param name="message">IClientMessage that client asked the response for from the server.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Response object that server executes for the given method.</returns>
        public override async Task ExecuteAsync(IClientMessage message, AppServiceRequest request, CancellationToken cancellationToken = default)
        {
            TResponse response = default;

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
                var valueSet = new ValueSet()
                {
                    { MessageConstants.MessageDataKey, JsonSerializer.Serialize(response) }
                };

                await request.SendResponseAsync(valueSet);
            }
        }

        /// <summary>
        /// Code that will be executed directly on the server.
        /// All handlers must implement this method.
        /// </summary>
        /// <param name="message">IClientMessage that client asked the response for from the server.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        protected abstract Task<TResponse> HandleAsync(TClientMessage message, CancellationToken cancellationToken = default);
        // => throw new NotImplementedException("Override HandleAsync and bring the implementation.");
    }
}
