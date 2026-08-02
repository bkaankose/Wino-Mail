using FluentAssertions;
using MimeKit;
using Wino.Core.Domain;
using Wino.Core.Synchronizers.Mail;
using Xunit;

namespace Wino.Core.Tests.Synchronizers;

public class ImapSmtpMessageTests
{
    [Fact]
    public void CreateSmtpMessage_RemovesDraftHeaderWithoutMutatingOriginal()
    {
        var draftMessage = new MimeMessage
        {
            Subject = "Draft",
            Body = new TextPart("plain") { Text = "Body" }
        };
        draftMessage.Headers.Add(Constants.WinoLocalDraftHeader, "local-draft-id");

        var smtpMessage = ImapSynchronizer.CreateSmtpMessage(draftMessage);

        smtpMessage.Headers.Contains(Constants.WinoLocalDraftHeader).Should().BeFalse();
        draftMessage.Headers[Constants.WinoLocalDraftHeader].Should().Be("local-draft-id");
        smtpMessage.Subject.Should().Be(draftMessage.Subject);
        smtpMessage.TextBody.TrimEnd().Should().Be(draftMessage.TextBody);
    }
}
