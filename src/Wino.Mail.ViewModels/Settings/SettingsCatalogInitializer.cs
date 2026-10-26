#nullable enable

using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Platform;
using Wino.Core.Domain.Models.Settings;

namespace Wino.Mail.ViewModels.Settings;

/// <summary>Gives <see cref="SettingsCatalog"/> the platform capabilities and the Wino Intelligence entitlement rule on every head.</summary>
public sealed class SettingsCatalogInitializer(IServiceProvider services) : IStartupInitializer
{
    public Task InitializeAsync()
    {
        var entitlement = services.GetService<IWinoAccountIntelligenceSnapshotService>();
        SettingsCatalog.Configure(services.GetService<IPlatformCapabilities>() ?? new PlatformCapabilities(),
            () => entitlement?.CurrentEntitlement?.CanAccessSurfaces ?? false);
        return Task.CompletedTask;
    }
}
