using System.Text;
using FluentAssertions;
using Wino.Core.Diagnostics;
using Xunit;

namespace Wino.Core.Tests.Diagnostics;

public sealed class WinoProtocolLoggerTests
{
    [Fact]
    public void ImapLogger_RedactsServerLiteralContent()
    {
        using var stream = new MemoryStream();

        using (var logger = new WinoProtocolLogger(stream, MailProtocol.Imap))
        {
            LogServer(logger, "* 1 FETCH (BODY[] {17}\r\n");
            LogServer(logger, "private body text");
            LogServer(logger, ")\r\n");
        }

        var log = Encoding.UTF8.GetString(stream.ToArray());

        log.Should().Contain("* 1 FETCH (BODY[] {17}");
        log.Should().Contain("[message content redacted]");
        log.Should().NotContain("private body text");
    }

    [Fact]
    public void SmtpLogger_RedactsDataPayload()
    {
        using var stream = new MemoryStream();

        using (var logger = new WinoProtocolLogger(stream, MailProtocol.Smtp))
        {
            LogClient(logger, "DATA\r\n");
            LogClient(logger, "Subject: private\r\n\r\nmessage body\r\n.\r\n");
        }

        var log = Encoding.UTF8.GetString(stream.ToArray());

        log.Should().Contain("DATA");
        log.Should().Contain("[message content redacted]");
        log.Should().NotContain("Subject: private");
        log.Should().NotContain("message body");
    }

    private static void LogClient(WinoProtocolLogger logger, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        logger.LogClient(bytes, 0, bytes.Length);
    }

    private static void LogServer(WinoProtocolLogger logger, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        logger.LogServer(bytes, 0, bytes.Length);
    }
}
