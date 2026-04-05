using System;

namespace Wino.Core.Domain.Models.Retry;

/// <summary>
/// Defines retry behavior for synchronization operations with exponential backoff.
/// </summary>
public class RetryPolicy
{
    private static readonly Random _jitterRandom = new();

    /// <summary>
    /// Gets or sets the maximum number of retry attempts. Default is 3.
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Gets or sets the initial delay before the first retry. Default is 1 second.
    /// </summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets or sets the multiplier for exponential backoff. Default is 2.0.
    /// Each retry delay = previous delay * multiplier.
    /// </summary>
    public double BackoffMultiplier { get; set; } = 2.0;

    /// <summary>
    /// Gets or sets the maximum delay between retries. Default is 2 minutes.
    /// </summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Gets or sets whether to add random jitter to delays to prevent thundering herd.
    /// Default is true.
    /// </summary>
    public bool UseJitter { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum jitter as a percentage of the delay (0.0 to 1.0).
    /// Default is 0.25 (25%).
    /// </summary>
    public double JitterFactor { get; set; } = 0.25;

    /// <summary>
    /// Calculates the delay for the given retry attempt using exponential backoff.
    /// </summary>
    /// <param name="retryAttempt">The retry attempt number (1-based).</param>
    /// <returns>The delay to wait before the retry.</returns>
    public TimeSpan GetDelay(int retryAttempt)
    {
        if (retryAttempt <= 0)
            return TimeSpan.Zero;

        // Calculate base delay with exponential backoff
        var baseDelayMs = InitialDelay.TotalMilliseconds * Math.Pow(BackoffMultiplier, retryAttempt - 1);

        // Apply max delay cap
        baseDelayMs = Math.Min(baseDelayMs, MaxDelay.TotalMilliseconds);

        // Apply jitter if enabled
        if (UseJitter)
        {
            var jitterRange = baseDelayMs * JitterFactor;
            var jitter = (_jitterRandom.NextDouble() * 2 - 1) * jitterRange; // +/- jitter range
            baseDelayMs = Math.Max(0, baseDelayMs + jitter);
        }

        return TimeSpan.FromMilliseconds(baseDelayMs);
    }

    /// <summary>
    /// Creates a default retry policy suitable for most synchronization operations.
    /// </summary>
    public static RetryPolicy Default => new();

    /// <summary>
    /// Creates an aggressive retry policy with more attempts and shorter delays.
    /// Suitable for transient network issues.
    /// </summary>
    public static RetryPolicy Aggressive => new()
    {
        MaxRetries = 5,
        InitialDelay = TimeSpan.FromMilliseconds(500),
        BackoffMultiplier = 1.5,
        MaxDelay = TimeSpan.FromSeconds(30)
    };

    /// <summary>
    /// Creates a conservative retry policy with longer delays.
    /// Suitable for rate limiting scenarios.
    /// </summary>
    public static RetryPolicy RateLimited => new()
    {
        MaxRetries = 3,
        InitialDelay = TimeSpan.FromSeconds(10),
        BackoffMultiplier = 2.0,
        MaxDelay = TimeSpan.FromMinutes(5)
    };

    /// <summary>
    /// Creates a no-retry policy that doesn't retry on failure.
    /// </summary>
    public static RetryPolicy NoRetry => new() { MaxRetries = 0 };
}
