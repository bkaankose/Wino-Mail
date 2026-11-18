#nullable enable
using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Services.Dav;
using Wino.Services.Security;

namespace Wino.Services.Platform;

public static class PortablePlatformRegistration
{
    /// <summary>
    /// The portable implementations every host without Windows storage registers the same way: macOS, iOS, Android and
    /// <c>Wino.Hosting</c>. Call it after <c>RegisterSharedServices</c>, because it replaces the configuration that registers.
    /// The Google and OAuth token stores live in <c>Wino.Authentication</c>, so each host still registers them.
    /// </summary>
    public static IServiceCollection AddPortablePlatformServices(this IServiceCollection services, IApplicationConfiguration configuration,
        ISecureItemStore secureItems, ISecretProtector protector, IAppMetadataService metadata)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(secureItems);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(metadata);

        var dataRoot = configuration.ApplicationDataFolderPath;

        services.AddSingleton<ISecureItemStore>(secureItems);
        services.AddSingleton<ISecretProtector>(protector);
        services.Replace(ServiceDescriptor.Singleton<IApplicationConfiguration>(configuration));

        services.AddSingleton<IConfigurationService>(_ => new JsonFileConfigurationService(dataRoot));
        services.AddSingleton<IPreferencesService, PreferencesService>();
        services.AddSingleton<IFileService, FileSystemFileService>();
        services.AddSingleton<IAccountCredentialPersistence, SecureItemAccountCredentialPersistence>();
        services.AddSingleton<IDavCredentialStore, SecureItemDavCredentialStore>();
        services.AddSingleton<IStatePersistanceService, ConfigurationStatePersistenceService>();
        services.AddSingleton<IAppMetadataService>(metadata);
        return services;
    }
}
