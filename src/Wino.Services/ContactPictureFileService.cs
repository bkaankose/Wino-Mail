using System;
using System.IO;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

/// <summary>
/// Stores contact pictures as JPEG files under {ApplicationDataFolderPath}/contacts/{fileId}.jpg.
/// This avoids base64 inline storage in SQLite that bloats all AccountContact queries.
/// </summary>
public class ContactPictureFileService : BaseDatabaseService, IContactPictureFileService
{
    private sealed class LegacyAccountContactPictureRow
    {
        public string Address { get; set; }
        public string Base64ContactPicture { get; set; }
    }

    private const string ContactsSubFolder = "contacts";

    private readonly string _contactPicturesFolder;
    private readonly ILogger _logger = Log.ForContext<ContactPictureFileService>();

    public ContactPictureFileService(IDatabaseService databaseService, IApplicationConfiguration applicationConfiguration)
        : base(databaseService)
    {
        _contactPicturesFolder = Path.Combine(applicationConfiguration.ApplicationDataFolderPath, ContactsSubFolder);
        EnsureContactPicturesFolder();
    }

    public string GetContactPicturePath(Guid fileId)
    {
        var path = BuildFilePath(fileId);
        return File.Exists(path) ? path : null;
    }

    public async Task<Guid> SaveContactPictureAsync(byte[] imageData)
    {
        var fileId = Guid.NewGuid();
        var filePath = BuildFilePath(fileId);

        EnsureContactPicturesFolder();

        await File.WriteAllBytesAsync(filePath, imageData).ConfigureAwait(false);
        return fileId;
    }

    public Task DeleteContactPictureAsync(Guid fileId)
    {
        var filePath = BuildFilePath(fileId);
        if (File.Exists(filePath))
            File.Delete(filePath);
        return Task.CompletedTask;
    }

    private void EnsureContactPicturesFolder()
    {
        Directory.CreateDirectory(_contactPicturesFolder);
    }

    private string BuildFilePath(Guid fileId) => Path.Combine(_contactPicturesFolder, $"{fileId}.jpg");
}
