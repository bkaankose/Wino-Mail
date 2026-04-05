using System;
using System.Collections.Generic;

namespace Wino.Core.Domain.Models.Connectivity;

/// <summary>
/// Represents the health status of an IMAP connection pool.
/// </summary>
public class ConnectionPoolHealth
{
    /// <summary>
    /// Gets or sets the total number of connections in the pool (including IDLE).
    /// </summary>
    public int TotalConnections { get; set; }

    /// <summary>
    /// Gets or sets the number of connections available for use.
    /// </summary>
    public int AvailableConnections { get; set; }

    /// <summary>
    /// Gets or sets the number of connections currently in use.
    /// </summary>
    public int InUseConnections { get; set; }

    /// <summary>
    /// Gets or sets the number of connections that have failed and need reconnection.
    /// </summary>
    public int FailedConnections { get; set; }

    /// <summary>
    /// Gets or sets the number of connections currently reconnecting.
    /// </summary>
    public int ReconnectingConnections { get; set; }

    /// <summary>
    /// Gets or sets whether the dedicated IDLE connection is active and listening.
    /// </summary>
    public bool IdleConnectionActive { get; set; }

    /// <summary>
    /// Gets or sets the timestamp of the last health check.
    /// </summary>
    public DateTime LastHealthCheck { get; set; }

    /// <summary>
    /// Gets or sets recent issues encountered by the pool.
    /// </summary>
    public List<string> RecentIssues { get; set; } = [];

    /// <summary>
    /// Gets whether the pool is healthy (has minimum required connections).
    /// </summary>
    public bool IsHealthy => AvailableConnections >= 1 && FailedConnections == 0;

    /// <summary>
    /// Gets a summary of the pool health.
    /// </summary>
    public string Summary => $"Total: {TotalConnections}, Available: {AvailableConnections}, InUse: {InUseConnections}, Failed: {FailedConnections}, IDLE: {(IdleConnectionActive ? "Active" : "Inactive")}";
}
