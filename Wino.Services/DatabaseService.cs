using System.IO;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

public interface IDatabaseService : IInitializeAsync
{
    IDbContextFactory<WinoDbContext> ContextFactory { get; }
}

public class DatabaseService : IDatabaseService, IDbContextFactory<WinoDbContext>
{
    private const string DatabaseName = "Wino180.db";

    private bool _isInitialized = false;
    private readonly IApplicationConfiguration _folderConfiguration;
    private string _databasePath;

    public IDbContextFactory<WinoDbContext> ContextFactory => this;

    public DatabaseService(IApplicationConfiguration folderConfiguration)
    {
        _folderConfiguration = folderConfiguration;
    }

    public async Task InitializeAsync()
    {
        if (_isInitialized)
            return;

        var publisherCacheFolder = _folderConfiguration.PublisherSharedFolderPath;
        _databasePath = Path.Combine(publisherCacheFolder, DatabaseName);

        // Test context creation and ensure database exists
        using var context = CreateDbContext();
        await context.Database.EnsureCreatedAsync();

        _isInitialized = true;
    }

    public WinoDbContext CreateDbContext()
    {
        if (string.IsNullOrEmpty(_databasePath))
        {
            // Fallback for design-time or before initialization
            var publisherCacheFolder = _folderConfiguration.PublisherSharedFolderPath;
            _databasePath = Path.Combine(publisherCacheFolder, DatabaseName);
        }

        var optionsBuilder = new DbContextOptionsBuilder<WinoDbContext>();
        optionsBuilder.UseSqlite($"Data Source={_databasePath}");

        return new WinoDbContext(optionsBuilder.Options);
    }
}
