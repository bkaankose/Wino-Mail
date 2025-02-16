using System;
using System.Linq;
using System.Net.Mail;

namespace Wino.Core.UWP.Services;

public static class ThumbnailService
{
    private static string[] knownCompanies = new string[]
    {
        "microsoft.com", "apple.com", "google.com", "steampowered.com", "airbnb.com", "youtube.com", "uber.com"
    };

    public static bool IsKnown(string mailHost) => !string.IsNullOrEmpty(mailHost) && knownCompanies.Contains(mailHost);

    public static string GetHost(string address)
    {
        if (string.IsNullOrEmpty(address))
            return string.Empty;

        if (address.Contains('@'))
        {
            var splitted = address.Split('@');

            if (splitted.Length >= 2 && !string.IsNullOrEmpty(splitted[1]))
            {
                try
                {
                    return new MailAddress(address).Host;
                }
                catch (Exception)
                {
                    // TODO: Exceptions are ignored for now.
                }
            }
        }

        return string.Empty;
    }

    public static Tuple<bool, string> CheckIsKnown(string host)
    {
        // Check known hosts.
        // Apply company logo if available.

        try
        {
            var last = host.Split('.');

            if (last.Length > 2)
                host = $"{last[last.Length - 2]}.{last[last.Length - 1]}";
        }
        catch (Exception)
        {
            return new Tuple<bool, string>(false, host);
        }

        return new Tuple<bool, string>(IsKnown(host), host);
    }

    public static string GetKnownHostImage(string host)
        => $"ms-appx:///Assets/Thumbnails/{host}.png";
}
