using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace Wino.Core.Http;

/// <summary>
/// DelegatingHandler that automatically handles Microsoft Graph API 429 rate limiting responses.
/// Integrates directly with the Graph SDK HTTP pipeline to provide transparent retry functionality.
/// 
/// Features:
/// - Intercepts 429 (Too Many Requests) HTTP responses before they become ServiceExceptions
/// - Respects Retry-After header from responses (both seconds and HTTP date formats)
/// - Maximum 3 retry attempts to prevent infinite loops
/// - Caps retry delays to 5 minutes maximum
/// - Uses 60-second default delay if no Retry-After header is provided
/// - Comprehensive logging for debugging and monitoring
/// - Thread-safe and cancellation token aware
/// - Integrates seamlessly with existing Graph SDK error handling
/// 
/// Usage:
/// Add to GraphServiceClient handlers in OutlookSynchronizer constructor:
/// 
/// var handlers = GraphClientFactory.CreateDefaultHandlers();
/// handlers.Add(new MicrosoftImmutableIdHandler());
/// handlers.Add(new GraphRateLimitHandler());
/// var httpClient = GraphClientFactory.Create(handlers);
/// </summary>
public class GraphRateLimitHandler : DelegatingHandler
{
    private static readonly ILogger _logger = Log.ForContext<GraphRateLimitHandler>();
    private const int MaxRetryAttempts = 3;
    private const int MaxDelaySeconds = 300; // 5 minutes cap
    private const int DefaultDelaySeconds = 60; // Default delay when no Retry-After header

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var attempt = 0;

        while (attempt <= MaxRetryAttempts)
        {
            HttpResponseMessage response;
            
            try
            {
                response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error sending request to {Uri} on attempt {Attempt}", request.RequestUri, attempt + 1);
                throw;
            }

            // Check if we got a 429 Too Many Requests response
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                if (attempt == MaxRetryAttempts)
                {
                    _logger.Warning("Max retry attempts ({MaxAttempts}) reached for rate limited request to {Uri}", 
                        MaxRetryAttempts, request.RequestUri);
                    return response; // Return the 429 response after max attempts
                }

                // Get the Retry-After header value
                var retryAfterSeconds = GetRetryAfterSeconds(response);
                
                if (retryAfterSeconds > 0)
                {
                    // Cap the delay to a reasonable maximum
                    var cappedDelay = Math.Min(retryAfterSeconds, MaxDelaySeconds);
                    
                    _logger.Information("Rate limited (429) - waiting {RetrySeconds} seconds before retry attempt {Attempt}/{MaxAttempts} for {Uri}", 
                        cappedDelay, attempt + 1, MaxRetryAttempts, request.RequestUri);
                    
                    await Task.Delay(TimeSpan.FromSeconds(cappedDelay), cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    _logger.Warning("Rate limited (429) but no valid Retry-After header found for {Uri} - using default {DefaultDelay} second delay", 
                        request.RequestUri, DefaultDelaySeconds);
                    
                    // Use a default delay if no Retry-After header is provided
                    await Task.Delay(TimeSpan.FromSeconds(DefaultDelaySeconds), cancellationToken).ConfigureAwait(false);
                }

                attempt++;
                response.Dispose(); // Dispose the 429 response before retry
                continue;
            }

            // Success or other error - return the response
            return response;
        }

        // This should never be reached, but just in case
        throw new InvalidOperationException("Rate limiting retry logic error");
    }

    /// <summary>
    /// Extracts the retry delay from the Retry-After header.
    /// Supports both seconds (integer) and HTTP date formats.
    /// </summary>
    /// <param name="response">The HTTP response containing Retry-After header</param>
    /// <returns>Number of seconds to wait, or 0 if header is missing or invalid</returns>
    private int GetRetryAfterSeconds(HttpResponseMessage response)
    {
        try
        {
            // Check if Retry-After header exists
            if (response.Headers.RetryAfter == null)
            {
                _logger.Debug("No Retry-After header found in response");
                return 0;
            }

            // Handle retry-after-seconds (integer)
            if (response.Headers.RetryAfter.Delta.HasValue)
            {
                var seconds = (int)response.Headers.RetryAfter.Delta.Value.TotalSeconds;
                _logger.Debug("Found Retry-After delta: {Seconds} seconds", seconds);
                return seconds;
            }

            // Handle retry-after-date (HTTP date)
            if (response.Headers.RetryAfter.Date.HasValue)
            {
                var retryAfterTime = response.Headers.RetryAfter.Date.Value;
                var delaySeconds = (int)(retryAfterTime - DateTimeOffset.UtcNow).TotalSeconds;
                _logger.Debug("Found Retry-After date: {Date}, calculated delay: {Seconds} seconds", retryAfterTime, delaySeconds);
                
                // Ensure we don't have a negative delay
                return Math.Max(0, delaySeconds);
            }

            _logger.Debug("Retry-After header present but no valid value found");
            return 0;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Error parsing Retry-After header");
            return 0;
        }
    }
}