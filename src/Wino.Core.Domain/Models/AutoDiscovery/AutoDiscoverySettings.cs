using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.AutoDiscovery;

public class AutoDiscoverySettings
{
    [JsonPropertyName("domain")]
    public string Domain { get; set; }

    [JsonPropertyName("password")]
    public string Password { get; set; }

    [JsonPropertyName("settings")]
    public List<AutoDiscoveryProviderSetting> Settings { get; set; }

    /// <summary>
    /// Gets whether this domain requires additional steps for password like app-specific password or sth.
    /// </summary>
    public bool IsPasswordSupportLinkAvailable => !string.IsNullOrEmpty(Password) && Uri.TryCreate(Password, UriKind.Absolute, out _);

    public AutoDiscoveryMinimalSettings UserMinimalSettings { get; set; }

    public CustomServerInformation ToServerInformation()
    {
        var imapSettings = GetImapSettings();
        var smtpSettings = GetSmptpSettings();

        if (imapSettings == null || smtpSettings == null) return null;

        string imapUrl = imapSettings.Address;
        string smtpUrl = smtpSettings.Address;

        string imapUsername = imapSettings.Username;
        string smtpUsername = smtpSettings.Username;

        int imapPort = imapSettings.Port;
        int smtpPort = smtpSettings.Port;

        var serverInfo = new CustomServerInformation
        {
            Id = Guid.NewGuid(),
            DisplayName = UserMinimalSettings.DisplayName,
            Address = UserMinimalSettings.Email,
            IncomingServerPassword = UserMinimalSettings.Password,
            OutgoingServerPassword = UserMinimalSettings.Password,
            IncomingAuthenticationMethod = GetAuthenticationMethod(imapSettings),
            OutgoingAuthenticationMethod = GetAuthenticationMethod(smtpSettings),
            OutgoingServerSocketOption = GetConnectionSecurity(smtpSettings.Secure),
            IncomingServerSocketOption = GetConnectionSecurity(imapSettings.Secure),
            IncomingServer = imapUrl,
            OutgoingServer = smtpUrl,
            IncomingServerPort = imapPort.ToString(),
            OutgoingServerPort = smtpPort.ToString(),
            IncomingServerType = Enums.CustomIncomingServerType.IMAP4,
            IncomingServerUsername = imapUsername,
            OutgoingServerUsername = smtpUsername,
            MaxConcurrentClients = 5,
            ConnectionPolicyVersion = ImapConnectionPolicyVersion.Corrected
        };

        return serverInfo;
    }

    public AutoDiscoveryProviderSetting GetImapSettings()
        => Settings?.Find(a => string.Equals(a.Protocol, "IMAP", StringComparison.OrdinalIgnoreCase));

    public AutoDiscoveryProviderSetting GetSmptpSettings()
        => Settings?.Find(a => string.Equals(a.Protocol, "SMTP", StringComparison.OrdinalIgnoreCase));

    private static ImapConnectionSecurity GetConnectionSecurity(string value)
    {
        var normalized = value?.Trim().Replace("_", "-").ToUpperInvariant();
        return normalized switch
        {
            "SSL" or "SSL/TLS" or "TLS" => ImapConnectionSecurity.SslTls,
            "STARTTLS" => ImapConnectionSecurity.StartTls,
            "NONE" or "PLAIN" => ImapConnectionSecurity.None,
            _ => ImapConnectionSecurity.Auto
        };
    }

    private static ImapAuthenticationMethod GetAuthenticationMethod(AutoDiscoveryProviderSetting setting)
    {
        var methods = setting.AuthenticationMethods ?? [];
        foreach (var value in methods)
        {
            var method = value?.Trim().ToLowerInvariant();
            if (method is "password-cleartext" or "plain" or "login") return ImapAuthenticationMethod.NormalPassword;
            if (method == "none") return ImapAuthenticationMethod.None;
            if (method == "ntlm") return ImapAuthenticationMethod.Ntlm;
            if (method == "cram-md5") return ImapAuthenticationMethod.CramMd5;
            if (method == "digest-md5") return ImapAuthenticationMethod.DigestMd5;
        }

        if (methods.Exists(value => value?.Contains("oauth", StringComparison.OrdinalIgnoreCase) == true))
            throw new NotSupportedException($"{setting.Protocol} autodiscovery only advertises OAuth authentication, which is not supported for custom accounts.");

        return ImapAuthenticationMethod.Auto;
    }
}
