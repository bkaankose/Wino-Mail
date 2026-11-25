using MailKit;
using MimeKit;

namespace Wino.Services.Models.MailItem;

/// <summary>
/// Encapsulates all required information to create a MimeMessage for IMAP synchronizer.
/// </summary>
public class ImapMessageCreationPackage
{
    public IMessageSummary MessageSummary { get; }
    public MimeMessage MimeMessage { get; }

    /// <summary>
    /// The whole header section (BODY[HEADER]) when the synchronizer re-read it because HEADER.FIELDS or
    /// ENVELOPE may carry downgraded characters; null otherwise.
    /// </summary>
    public HeaderList RawHeaders { get; }

    public ImapMessageCreationPackage(IMessageSummary messageSummary, MimeMessage mimeMessage, HeaderList rawHeaders = null)
    {
        MessageSummary = messageSummary;
        MimeMessage = mimeMessage;
        RawHeaders = rawHeaders;
    }
}
