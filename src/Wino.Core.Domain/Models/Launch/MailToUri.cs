using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

namespace Wino.Core.Domain.Models.Launch;

public class MailToUri
{
    public string Subject { get; private set; }
    public string Body { get; private set; }
    public string InReplyTo { get; private set; }
    public string References { get; private set; }
    public List<string> To { get; } = [];
    public List<string> Cc { get; } = [];
    public List<string> Bcc { get; } = [];
    public Dictionary<string, string> OtherParameters { get; } = [];

    public MailToUri(string mailToUrl)
    {
        ParseMailToUrl(mailToUrl);
    }

    private void ParseMailToUrl(string mailToUrl)
    {
        if (string.IsNullOrWhiteSpace(mailToUrl))
            throw new ArgumentException("mailtoUrl cannot be null or empty.", nameof(mailToUrl));

        if (!mailToUrl.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("URL must start with 'mailto:'.", nameof(mailToUrl));

        var mailToWithoutScheme = mailToUrl.Substring(7); // Remove "mailto:"
        var queryStart = mailToWithoutScheme.IndexOf('?');
        var addresses = queryStart < 0 ? mailToWithoutScheme : mailToWithoutScheme.Substring(0, queryStart);

        if (!string.IsNullOrEmpty(addresses))
        {
            To.AddRange(addresses.Split(',').Select(email => HttpUtility.UrlDecode(email).Trim()));
        }

        if (queryStart < 0)
        {
            return;
        }

        var parameters = mailToWithoutScheme.Substring(queryStart + 1).Split('&');

        foreach (var parameter in parameters)
        {
            var separator = parameter.IndexOf('=');
            if (separator <= 0)
                continue;

            var key = parameter.Substring(0, separator).ToLowerInvariant();
            var rawValue = parameter.Substring(separator + 1);
            var value = HttpUtility.UrlDecode(rawValue);

            switch (key)
            {
                case "to":
                    To.AddRange(value.Split(',').Select(email => email.Trim()));
                    break;
                case "subject":
                    Subject = value;
                    break;
                case "body":
                    Body = value;
                    break;
                case "cc":
                    Cc.AddRange(value.Split(',').Select(email => email.Trim()));
                    break;
                case "bcc":
                    Bcc.AddRange(value.Split(',').Select(email => email.Trim()));
                    break;
                // RFC 6068 gives '+' no meaning, and Message-IDs often contain one (CAJ+...@mail.gmail.com).
                case "in-reply-to":
                    InReplyTo = Uri.UnescapeDataString(rawValue);
                    break;
                case "references":
                    References = Uri.UnescapeDataString(rawValue);
                    break;
                default:
                    OtherParameters[key] = value;
                    break;
            }
        }
    }
}
