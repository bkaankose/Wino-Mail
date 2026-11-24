using System;
using System.Linq;
using MimeKit;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Reader;
using Wino.Services.Interfaces;

namespace Wino.Services;

/// <summary>
/// The List-Unsubscribe reading of the apps that still render with a web view (macOS, iOS, Android, Linux and the
/// backend host). The Windows app registers the mail engine's reader instead, which also follows RFC 2369 parsing and
/// ordering and checks RFC 8058 one-click strictly. Remove this when every head uses the engine.
/// </summary>
public sealed class HeaderUnsubscribeReader : IMailUnsubscribeReader
{
    public UnsubscribeInfo Read(MimeMessage message)
    {
        if (message?.Headers.Contains(HeaderId.ListUnsubscribe) != true)
            return null;

        var unsubscribeLinks = message.Headers[HeaderId.ListUnsubscribe]
            .Normalize()
            .Split([','], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim().Trim(['<', '>']));

        var httpLink = unsubscribeLinks.FirstOrDefault(x => x.StartsWith("http", StringComparison.OrdinalIgnoreCase));
        var mailToLink = unsubscribeLinks.FirstOrDefault(x => x.StartsWith("mailto", StringComparison.OrdinalIgnoreCase));
        // RFC 8058 section 3.1: one click needs an https address.
        var isOneClick = httpLink != null
            && httpLink.StartsWith("https:", StringComparison.OrdinalIgnoreCase)
            && message.Headers.Contains(HeaderId.ListUnsubscribePost);

        var info = new UnsubscribeInfo
        {
            HttpLink = httpLink,
            MailToLink = mailToLink,
            IsOneClick = isOneClick,
            OneClickUrl = isOneClick ? httpLink : null
        };

        // The order these apps have always used: one click, then the web page, then a mail.
        if (isOneClick)
            info.Methods.Add(new UnsubscribeOption { Method = UnsubscribeMethod.OneClick, Uri = httpLink });

        if (httpLink != null)
            info.Methods.Add(new UnsubscribeOption { Method = UnsubscribeMethod.Website, Uri = httpLink });

        if (mailToLink != null)
            info.Methods.Add(new UnsubscribeOption { Method = UnsubscribeMethod.Mail, Uri = mailToLink });

        return info.CanUnsubscribe ? info : null;
    }
}
