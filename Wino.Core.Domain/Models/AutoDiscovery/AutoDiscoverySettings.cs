using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Models.AutoDiscovery
{
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
                IncomingAuthenticationMethod = Enums.ImapAuthenticationMethod.Auto,
                OutgoingAuthenticationMethod = Enums.ImapAuthenticationMethod.Auto,
                OutgoingServerSocketOption = Enums.ImapConnectionSecurity.Auto,
                IncomingServerSocketOption = Enums.ImapConnectionSecurity.Auto,
                IncomingServer = imapUrl,
                OutgoingServer = smtpUrl,
                IncomingServerPort = imapPort.ToString(),
                OutgoingServerPort = smtpPort.ToString(),
                IncomingServerType = Enums.CustomIncomingServerType.IMAP4,
                IncomingServerUsername = imapUsername,
                OutgoingServerUsername = smtpUsername,
                MaxConcurrentClients = 5
            };

            return serverInfo;
        }

        public AutoDiscoveryProviderSetting GetImapSettings()
            => Settings?.Find(a => a.Protocol == "IMAP");

        public AutoDiscoveryProviderSetting GetSmptpSettings()
            => Settings?.Find(a => a.Protocol == "SMTP");
    }
}
