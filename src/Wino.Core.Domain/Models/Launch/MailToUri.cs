using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

namespace Wino.Core.Domain.Models.Launch;

public class MailToUri
{
    public string Subject { get; private set; }
    public string Body { get; private set; }
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
        var components = mailToWithoutScheme.Split('?');
        if (!string.IsNullOrEmpty(components[0]))
        {
            To.AddRange(components[0].Split(',').Select(email => HttpUtility.UrlDecode(email).Trim()));
        }

        if (components.Length <= 1)
        {
            return;
        }

        var parameters = components[1].Split('&');

        foreach (var parameter in parameters)
        {
            var keyValue = parameter.Split('=');
            if (keyValue.Length != 2)
                continue;

            var key = keyValue[0].ToLowerInvariant();
            var value = HttpUtility.UrlDecode(keyValue[1]);

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
                default:
                    OtherParameters[key] = value;
                    break;
            }
        }
    }
}
