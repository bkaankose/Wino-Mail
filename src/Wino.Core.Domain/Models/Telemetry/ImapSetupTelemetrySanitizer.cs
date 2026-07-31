using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Models.Telemetry;

public static class ImapSetupTelemetrySanitizer
{
    public static readonly IReadOnlySet<string> AllowedKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "event_name",
        "feature",
        "setup_mode",
        "provider",
        "special_provider",
        "result",
        "failure_stage",
        "failure_category",
        "exception_type",
        "certificate_action",
        "certificate_error_kind",
        "mail_enabled",
        "calendar_enabled",
        "calendar_support_mode",
        "incoming_server_host",
        "incoming_server_domain",
        "incoming_server_kind",
        "incoming_server_port",
        "incoming_security",
        "incoming_auth",
        "outgoing_server_host",
        "outgoing_server_domain",
        "outgoing_server_kind",
        "outgoing_server_port",
        "outgoing_security",
        "outgoing_auth",
        "caldav_host",
        "caldav_domain",
        "caldav_scheme",
        "max_concurrent_clients_bucket",
        "proxy_configured"
    };

    public static Dictionary<string, string> CreateBaseProperties(
        string setupMode,
        string provider,
        string specialProvider,
        bool mailEnabled,
        bool calendarEnabled)
        => FilterAllowed(new Dictionary<string, string>
        {
            ["feature"] = "imap_setup",
            ["setup_mode"] = NormalizeValue(setupMode),
            ["provider"] = NormalizeValue(provider),
            ["special_provider"] = NormalizeValue(specialProvider),
            ["mail_enabled"] = mailEnabled.ToString(CultureInfo.InvariantCulture),
            ["calendar_enabled"] = calendarEnabled.ToString(CultureInfo.InvariantCulture)
        });

    public static Dictionary<string, string> CreateServerProperties(CustomServerInformation serverInformation)
    {
        var properties = new Dictionary<string, string>
        {
            ["feature"] = "imap_setup"
        };

        if (serverInformation == null)
            return properties;

        AddHostProperties(properties, "incoming_server", serverInformation.IncomingServer);
        properties["incoming_server_port"] = NormalizePort(serverInformation.IncomingServerPort);
        properties["incoming_security"] = serverInformation.IncomingServerSocketOption.ToString();
        properties["incoming_auth"] = serverInformation.IncomingAuthenticationMethod.ToString();

        AddHostProperties(properties, "outgoing_server", serverInformation.OutgoingServer);
        properties["outgoing_server_port"] = NormalizePort(serverInformation.OutgoingServerPort);
        properties["outgoing_security"] = serverInformation.OutgoingServerSocketOption.ToString();
        properties["outgoing_auth"] = serverInformation.OutgoingAuthenticationMethod.ToString();

        properties["calendar_support_mode"] = serverInformation.CalendarSupportMode.ToString();
        properties["max_concurrent_clients_bucket"] = GetClientCountBucket(serverInformation.MaxConcurrentClients);
        properties["proxy_configured"] = (!string.IsNullOrWhiteSpace(serverInformation.ProxyServer)).ToString(CultureInfo.InvariantCulture);

        AddCalDavProperties(properties, serverInformation.CalDavServiceUrl);

        return FilterAllowed(properties);
    }

    public static Dictionary<string, string> FilterAllowed(IReadOnlyDictionary<string, string> properties)
    {
        var safeProperties = new Dictionary<string, string>(StringComparer.Ordinal);

        if (properties == null)
            return safeProperties;

        foreach (var property in properties)
        {
            if (!AllowedKeys.Contains(property.Key) || property.Value == null)
                continue;

            safeProperties[property.Key] = NormalizeValue(property.Value);
        }

        return safeProperties;
    }

    public static string NormalizeHost(string value)
    {
        var host = ExtractHost(value);
        if (string.IsNullOrWhiteSpace(host))
            return string.Empty;

        if (IPAddress.TryParse(host, out _))
            return "ip_address";

        return host.ToLowerInvariant();
    }

    public static string GetDomain(string value)
    {
        var host = NormalizeHost(value);
        if (string.IsNullOrWhiteSpace(host) || host == "ip_address")
            return host;

        var parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 2
            ? host
            : $"{parts[^2]}.{parts[^1]}";
    }

    private static void AddHostProperties(IDictionary<string, string> properties, string prefix, string hostValue)
    {
        var host = NormalizeHost(hostValue);
        properties[$"{prefix}_host"] = host;
        properties[$"{prefix}_domain"] = GetDomain(hostValue);
        properties[$"{prefix}_kind"] = host == "ip_address" ? "ip_address" : "hostname";
    }

    private static void AddCalDavProperties(IDictionary<string, string> properties, string serviceUrl)
    {
        if (!Uri.TryCreate(serviceUrl, UriKind.Absolute, out var uri))
            return;

        properties["caldav_scheme"] = uri.Scheme.ToLowerInvariant();
        properties["caldav_host"] = NormalizeHost(uri.Host);
        properties["caldav_domain"] = GetDomain(uri.Host);
    }

    private static string ExtractHost(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var candidate = value.Trim();

        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            candidate = uri.Host;

        var atIndex = candidate.LastIndexOf('@');
        if (atIndex >= 0 && atIndex < candidate.Length - 1)
            candidate = candidate[(atIndex + 1)..];

        var slashIndex = candidate.IndexOfAny(['/', '\\']);
        if (slashIndex >= 0)
            candidate = candidate[..slashIndex];

        candidate = candidate.Trim('[', ']');

        var colonIndex = candidate.LastIndexOf(':');
        if (colonIndex > 0 && candidate.Count(c => c == ':') == 1)
            candidate = candidate[..colonIndex];

        return candidate.Trim().TrimEnd('.');
    }

    private static string NormalizePort(string port)
        => int.TryParse(port, NumberStyles.Integer, CultureInfo.InvariantCulture, out var portNumber) &&
           portNumber > 0 &&
           portNumber <= 65535
            ? portNumber.ToString(CultureInfo.InvariantCulture)
            : "invalid";

    private static string NormalizeValue(string value)
        => string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();

    private static string GetClientCountBucket(int count)
        => count switch
        {
            <= 0 => "unset",
            1 => "1",
            <= 3 => "2-3",
            <= 5 => "4-5",
            <= 10 => "6-10",
            _ => "11+"
        };
}
