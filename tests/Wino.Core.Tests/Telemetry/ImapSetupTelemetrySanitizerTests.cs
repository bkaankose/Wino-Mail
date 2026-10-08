using FluentAssertions;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Telemetry;
using Xunit;

namespace Wino.Core.Tests.Telemetry;

public sealed class ImapSetupTelemetrySanitizerTests
{
    [Fact]
    public void FilterAllowed_DropsSensitiveAndUnknownFields()
    {
        var properties = ImapSetupTelemetrySanitizer.FilterAllowed(new Dictionary<string, string>
        {
            ["incoming_server_host"] = "imap.example.com",
            ["incoming_server_port"] = "993",
            ["username"] = "person@example.com",
            ["password"] = "secret",
            ["token"] = "token-value",
            ["email"] = "person@example.com",
            ["caldav_path"] = "/users/person@example.com/calendar",
            ["message_content"] = "hello",
            ["unknown_key"] = "unknown"
        });

        properties.Should().Contain("incoming_server_host", "imap.example.com");
        properties.Should().Contain("incoming_server_port", "993");
        properties.Keys.Should().NotContain(["username", "password", "token", "email", "caldav_path", "message_content", "unknown_key"]);
    }

    [Fact]
    public void CreateServerProperties_SanitizesHostsAndCalDavUrl()
    {
        var serverInformation = new CustomServerInformation
        {
            IncomingServer = "IMAP.EXAMPLE.COM:993",
            IncomingServerPort = "993",
            IncomingAuthenticationMethod = ImapAuthenticationMethod.NormalPassword,
            IncomingServerSocketOption = ImapConnectionSecurity.SslTls,
            OutgoingServer = "smtp.example.com",
            OutgoingServerPort = "587",
            OutgoingAuthenticationMethod = ImapAuthenticationMethod.Auto,
            OutgoingServerSocketOption = ImapConnectionSecurity.StartTls,
            CalDavServiceUrl = "https://calendar.example.com/users/person@example.com/private?token=secret",
            CalendarSupportMode = ImapCalendarSupportMode.CalDav,
            ProxyServer = "proxy.example.com",
            MaxConcurrentClients = 5
        };

        var properties = ImapSetupTelemetrySanitizer.CreateServerProperties(serverInformation);

        properties["incoming_server_host"].Should().Be("imap.example.com");
        properties["incoming_server_domain"].Should().Be("example.com");
        properties["outgoing_server_host"].Should().Be("smtp.example.com");
        properties["caldav_host"].Should().Be("calendar.example.com");
        properties["caldav_domain"].Should().Be("example.com");
        properties.Keys.Should().NotContain(key => key.Contains("url", StringComparison.OrdinalIgnoreCase));
        properties.Values.Should().NotContain(value => value.Contains("person", StringComparison.OrdinalIgnoreCase));
        properties.Values.Should().NotContain(value => value.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NormalizeHost_ReportsIpAddressWithoutLeakingAddress()
    {
        ImapSetupTelemetrySanitizer.NormalizeHost("192.168.1.20").Should().Be("ip_address");
        ImapSetupTelemetrySanitizer.GetDomain("192.168.1.20").Should().Be("ip_address");
    }
}
