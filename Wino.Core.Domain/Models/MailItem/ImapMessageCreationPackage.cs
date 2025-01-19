using MailKit;
using MimeKit;

namespace Wino.Core.Domain.Models.MailItem
{
    /// <summary>
    /// Encapsulates all required information to create a MimeMessage for IMAP synchronizer.
    /// </summary>
    public class ImapMessageCreationPackage
    {
        public IMessageSummary MessageSummary { get; }
        public MimeMessage MimeMessage { get; }

        public ImapMessageCreationPackage(IMessageSummary messageSummary, MimeMessage mimeMessage)
        {
            MessageSummary = messageSummary;
            MimeMessage = mimeMessage;
        }
    }
}
