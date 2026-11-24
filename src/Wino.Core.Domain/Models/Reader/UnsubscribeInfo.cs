using System.Collections.Generic;

namespace Wino.Core.Domain.Models.Reader;

/// <summary>
/// How to unsubscribe from the list a message came from, read from its List-Unsubscribe header. The Windows app takes it
/// from the mail engine; the apps that still render with a web view read the header in Wino.Services.
/// </summary>
public class UnsubscribeInfo
{
    /// <summary>
    /// Every usable method, the preferred one first. A caller acts on <see cref="Preferred"/>.
    /// </summary>
    public List<UnsubscribeOption> Methods { get; set; } = [];
    public UnsubscribeOption Preferred => Methods.Count > 0 ? Methods[0] : null;

    /// <summary>
    /// True when the one-click POST (RFC 8058) may be sent to <see cref="OneClickUrl"/>.
    /// </summary>
    public bool IsOneClick { get; set; }
    public string OneClickUrl { get; set; }

    /// <summary>
    /// True when a DKIM-Signature of the message lists List-Unsubscribe and List-Unsubscribe-Post. Declared, not verified.
    /// </summary>
    public bool OneClickHeadersSigned { get; set; }

    public string HttpLink { get; set; }
    public string MailToLink { get; set; }
    public string ListId { get; set; }

    public bool CanUnsubscribe => Preferred != null;
}
