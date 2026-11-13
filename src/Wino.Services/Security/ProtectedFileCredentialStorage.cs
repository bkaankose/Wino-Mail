#nullable enable
using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;
using Wino.Services.Dav;

namespace Wino.Services.Security;

/// <summary>
/// Credential storage for a host whose secure items are <see cref="ProtectedFileSecureItemStore"/> files under
/// LocalState\secure and whose earlier releases kept plaintext database columns and DPAPI DAV files (Windows).
/// The host registers its <see cref="ISecretProtector"/> and, separately, its Google token store.
/// </summary>
public static class ProtectedFileCredentialStorage
{
    public const string SecureItemFolderName = "secure";

    public static string GetSecureItemRoot(IApplicationConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // An empty path would resolve "secure" against the working directory.
        if (string.IsNullOrWhiteSpace(configuration.ApplicationDataFolderPath))
            throw new InvalidOperationException("The application LocalState path is not initialized.");

        return Path.Combine(configuration.ApplicationDataFolderPath, SecureItemFolderName);
    }

    /// <summary>Persistence that keeps legacy plaintext readable and reports it to <paramref name="legacyPlaintextFound"/>.</summary>
    public static SecureItemAccountCredentialPersistence CreateLegacyAwarePersistence(
        ISecureItemStore store, Action<StoredCredentialKind, Guid>? legacyPlaintextFound = null)
        => new(store, new AccountCredentialPersistenceOptions
        {
            AcceptLegacyPlaintext = true,
            TreatUnreadableAsMissing = true,
            LegacyPlaintextFound = legacyPlaintextFound
        });

    public static IServiceCollection AddProtectedFileCredentialStorage(this IServiceCollection services)
    {
        services.AddSingleton(provider => new ProtectedFileSecureItemStore(
            GetSecureItemRoot(provider.GetRequiredService<IApplicationConfiguration>()),
            provider.GetRequiredService<ISecretProtector>()));
        services.AddSingleton<ISecureItemStore>(provider => provider.GetRequiredService<ProtectedFileSecureItemStore>());

        services.AddSingleton(provider => CreateLegacyAwarePersistence(
            provider.GetRequiredService<ISecureItemStore>(),
            (kind, accountId) => provider.GetService<ICredentialStoreMigrationService>()?.ReportLegacyPlaintext(kind, accountId)));
        services.AddSingleton<IAccountCredentialPersistence>(provider => provider.GetRequiredService<SecureItemAccountCredentialPersistence>());

        services.AddSingleton(provider => new LegacyMigratingDavCredentialStore(
            provider.GetRequiredService<ISecureItemStore>(),
            new DavCredentialStore(provider.GetRequiredService<IApplicationConfiguration>(), provider.GetRequiredService<ISecretProtector>())));
        services.AddSingleton<IDavCredentialStore>(provider => provider.GetRequiredService<LegacyMigratingDavCredentialStore>());
        services.AddSingleton<ILegacyCredentialFileMigration>(provider => provider.GetRequiredService<LegacyMigratingDavCredentialStore>());

        services.AddSingleton<ICredentialStoreMigrationService>(provider => new CredentialStoreMigrationService(
            provider.GetRequiredService<IDatabaseService>(),
            provider.GetRequiredService<SecureItemAccountCredentialPersistence>(),
            provider.GetRequiredService<IAccountService>,
            provider.GetRequiredService<IWinoAccountSessionService>,
            provider.GetServices<ILegacyCredentialFileMigration>()));

        return services;
    }
}
