using System;
using System.Net.Mail;

namespace Wino.Core.Domain.Validation;

public static class MailAccountAddressValidator
{
    public static bool IsValid(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return false;

        var trimmedAddress = address.Trim();

        if (trimmedAddress.Contains('\r') || trimmedAddress.Contains('\n'))
            return false;

        try
        {
            var parsedAddress = new MailAddress(trimmedAddress);
            return parsedAddress.Address.Equals(trimmedAddress, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static bool TryGetDomain(string address, out string domain)
    {
        domain = string.Empty;

        if (!IsValid(address))
            return false;

        var trimmedAddress = address.Trim();
        var separatorIndex = trimmedAddress.LastIndexOf('@');

        if (separatorIndex <= 0 || separatorIndex >= trimmedAddress.Length - 1)
            return false;

        domain = trimmedAddress[(separatorIndex + 1)..];
        return !string.IsNullOrWhiteSpace(domain);
    }

    public static bool IsImplicitlyResolvableHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        var normalizedHost = host.Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(normalizedHost))
            return false;

        if (normalizedHost.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        var hostType = Uri.CheckHostName(normalizedHost);
        if (hostType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
            return true;

        return normalizedHost.IndexOf('.') < 0;
    }
}
