using System.Collections.Generic;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Reader;

public class UnsubscribeOption
{
    public UnsubscribeMethod Method { get; set; }
    public string Uri { get; set; }

    /// <summary>
    /// The recipients, subject and body of a mailto method (RFC 6068). Empty for the other methods.
    /// </summary>
    public List<string> MailRecipients { get; set; } = [];
    public string MailSubject { get; set; }
    public string MailBody { get; set; }
}
