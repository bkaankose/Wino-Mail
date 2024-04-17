using MailKit;

namespace Wino.Core.Mime
{
    /// <summary>
    /// Encapsulates all required information to create a MimeMessage for IMAP synchronizer.
    /// </summary>
    public record ImapMessageCreationPackage(IMessageSummary MessageSummary, IMailFolder MailFolder);
}
