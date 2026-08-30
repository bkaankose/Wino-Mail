using System.Text.Json;
using FluentAssertions;
using Moq;
using Wino.Authentication;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Authentication;
using Wino.Core.Domain.Models.Migration;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class AuthenticationTokenMigrationServiceTests
{
    [Fact]
    public async Task PrepareAndFinalizeAsync_MovesOutlookAndStoreGmailTokensIntoLocalState()
    {
        var packageDataPath = Path.Combine(Path.GetTempPath(), $"wino-token-migration-{Guid.NewGuid():N}");
        var localStatePath = Path.Combine(packageDataPath, "LocalState");
        var publisherPath = Path.Combine(packageDataPath, "Publisher", "WinoShared");
        var legacyGmailStorePath = Path.Combine(
            packageDataPath,
            "LocalCache",
            "Roaming",
            AuthenticationTokenStorePaths.GmailTokenStoreFolderName);
        var gmailAccountId = Guid.NewGuid();

        try
        {
            Directory.CreateDirectory(localStatePath);
            Directory.CreateDirectory(publisherPath);
            Directory.CreateDirectory(legacyGmailStorePath);

            var configuration = new ApplicationConfiguration
            {
                ApplicationDataFolderPath = localStatePath,
                PublisherSharedFolderPath = publisherPath
            };
            var legacyOutlookPath = AuthenticationTokenStorePaths.GetLegacyOutlookTokenCachePath(configuration);
            var legacyGmailPath = AuthenticationTokenStorePaths.GetLegacyGoogleTokenPath(
                legacyGmailStorePath,
                gmailAccountId);
            await File.WriteAllBytesAsync(legacyOutlookPath, [1, 3, 3, 7]);
            await File.WriteAllTextAsync(legacyGmailPath, """
                {
                  "access_token": "legacy-access-token",
                  "refresh_token": "legacy-refresh-token",
                  "expires_in": 3600,
                  "scope": "mail calendar",
                  "IssuedUtc": "2099-08-30T12:00:00Z"
                }
                """);
            var publisherGmailStorePath = AuthenticationTokenStorePaths
                .GetLegacyPublisherGmailTokenStorePath(configuration);
            Directory.CreateDirectory(publisherGmailStorePath);
            await File.WriteAllTextAsync(Path.Combine(publisherGmailStorePath, "orphaned-token.json"), "sensitive");

            var accounts = new[]
            {
                new MigrationAccountOptions(
                    gmailAccountId,
                    "Gmail",
                    "gmail@example.com",
                    MailProviderType.Gmail,
                    true,
                    true,
                    true)
            };
            var service = new AuthenticationTokenMigrationService(configuration);

            var result = await service.PrepareAsync(accounts);

            result.OutlookCacheMigrated.Should().BeTrue();
            result.ReusableGmailAccountIds.Should().ContainSingle().Which.Should().Be(gmailAccountId);
            var localOutlookPath = AuthenticationTokenStorePaths.GetOutlookTokenCachePath(configuration);
            var localGmailPath = AuthenticationTokenStorePaths.GetGmailTokenPath(configuration, gmailAccountId);
            (await File.ReadAllBytesAsync(localOutlookPath)).Should().Equal(1, 3, 3, 7);
            File.Exists(legacyOutlookPath).Should().BeTrue("legacy files remain available until final validation");
            File.Exists(legacyGmailPath).Should().BeTrue("legacy files remain available until final validation");

            using (var tokenDocument = JsonDocument.Parse(await File.ReadAllTextAsync(localGmailPath)))
            {
                tokenDocument.RootElement.GetProperty("AccessToken").GetString().Should().Be("legacy-access-token");
                tokenDocument.RootElement.GetProperty("RefreshToken").GetString().Should().Be("legacy-refresh-token");
                tokenDocument.RootElement.GetProperty("Scopes").GetArrayLength().Should().Be(2);
            }

            var authenticator = new GmailAuthenticator(
                new MailAuthenticatorConfiguration(configuration),
                Mock.Of<INativeAppService>());
            var tokenInformation = await authenticator.GetTokenInformationAsync(new MailAccount
            {
                Id = gmailAccountId,
                Address = "gmail@example.com",
                ProviderType = MailProviderType.Gmail,
                IsMailAccessGranted = true
            });
            tokenInformation.AccessToken.Should().Be("legacy-access-token");

            await service.FinalizeAsync(accounts);

            File.Exists(localOutlookPath).Should().BeTrue();
            File.Exists(localGmailPath).Should().BeTrue();
            File.Exists(legacyOutlookPath).Should().BeFalse();
            File.Exists(legacyGmailPath).Should().BeFalse();
            Directory.Exists(publisherGmailStorePath).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(packageDataPath))
                Directory.Delete(packageDataPath, recursive: true);
        }
    }
}
