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

    [Theory]
    [InlineData("RETR 7\r\n")]
    [InlineData("TOP 7 0\r\n")]
    public void Pop3Logger_RedactsRetrievedMessageContent(string command)
    {
        using var stream = new MemoryStream();

        using (var logger = new WinoProtocolLogger(stream, MailProtocol.Pop3))
        {
            LogClient(logger, command);
            LogServer(logger, "+OK message follows\r\n");
            LogServer(logger, "Subject: private\r\n\r\nmessage body\r\n.\r\n");
        }

        var log = Encoding.UTF8.GetString(stream.ToArray());

        log.Should().Contain(command.Trim());
        log.Should().Contain("+OK message follows");
        log.Should().Contain("[message content redacted]");
        log.Should().NotContain("Subject: private");
        log.Should().NotContain("message body");
    }

    [Fact]
    public void CreateAccountLogger_WritesProtocolsToSeparateAccountFiles()
    {
        var applicationDataPath = Path.Combine(Path.GetTempPath(), $"wino-protocol-{Guid.NewGuid():N}");
        var accountId = Guid.NewGuid();

        try
        {
            using (var imapLogger = WinoProtocolLogger.CreateAccountLogger(
                       applicationDataPath,
                       accountId,
                       MailProtocol.Imap))
            {
                LogClient(imapLogger, "A1 NOOP\r\n");
            }

            using (var smtpLogger = WinoProtocolLogger.CreateAccountLogger(
                       applicationDataPath,
                       accountId,
                       MailProtocol.Smtp))
            {
                LogClient(smtpLogger, "EHLO localhost\r\n");
            }

            using (var pop3Logger = WinoProtocolLogger.CreateAccountLogger(
                       applicationDataPath,
                       accountId,
                       MailProtocol.Pop3))
            {
                LogClient(pop3Logger, "UIDL\r\n");
            }

            var accountFolder = WinoProtocolLogger.GetAccountLogFolder(applicationDataPath, accountId);
            var imapPath = Path.Combine(accountFolder, WinoProtocolLogger.ImapProtocolLogFileName);
            var smtpPath = Path.Combine(accountFolder, WinoProtocolLogger.SmtpProtocolLogFileName);
            var pop3Path = Path.Combine(accountFolder, WinoProtocolLogger.Pop3ProtocolLogFileName);

            File.ReadAllText(imapPath).Should().Contain("A1 NOOP").And.NotContain("EHLO localhost").And.NotContain("UIDL");
            File.ReadAllText(smtpPath).Should().Contain("EHLO localhost").And.NotContain("A1 NOOP").And.NotContain("UIDL");
            File.ReadAllText(pop3Path).Should().Contain("UIDL").And.NotContain("A1 NOOP").And.NotContain("EHLO localhost");
        }
        finally
        {
            if (Directory.Exists(applicationDataPath))
                Directory.Delete(applicationDataPath, recursive: true);
        }
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
