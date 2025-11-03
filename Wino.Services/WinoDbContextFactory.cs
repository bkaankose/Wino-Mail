using System;
using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

/// <summary>
/// Factory for creating WinoDbContext instances at design-time (for migrations and tooling).
/// This allows EF Core tools to create a DbContext without running the full application.
/// </summary>
public class WinoDbContextFactory : IDesignTimeDbContextFactory<WinoDbContext>
{
    public WinoDbContext CreateDbContext(string[] args)
    {
        // TODO: EFCore - This uses a hardcoded path for design-time only.
        // At runtime, the actual path will be provided by IApplicationConfiguration.
        // The publisher cache folder path should be: ApplicationData.Current.GetPublisherCacheFolder("WinoSharedFolder").Path
        // For migrations to work, you may need to temporarily set this to a valid local path.
        
        var optionsBuilder = new DbContextOptionsBuilder<WinoDbContext>();
        
        // Design-time database path - update this as needed for local development
        var databasePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages",
            "WinoSharedFolder",
            "Wino180.db"
        );

        optionsBuilder.UseSqlite($"Data Source={databasePath}");

        return new WinoDbContext(optionsBuilder.Options);
    }
}
