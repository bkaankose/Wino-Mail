using System;
using System.IO;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Entities.Shared;
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
        Directory.CreateDirectory(_contactPicturesFolder);
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

    public async Task MigrateBase64PicturesAsync()
    {
        try
        {
            var contacts = await Connection
                .QueryAsync<LegacyAccountContactPictureRow>(
                    "SELECT Address, Base64ContactPicture FROM AccountContact WHERE Base64ContactPicture IS NOT NULL AND ContactPictureFileId IS NULL")
                .ConfigureAwait(false);

            foreach (var contact in contacts)
            {
                try
                {
                    var base64 = contact.Base64ContactPicture;
                    if (string.IsNullOrEmpty(base64))
                        continue;

                    var bytes = Convert.FromBase64String(base64);
                    var fileId = await SaveContactPictureAsync(bytes).ConfigureAwait(false);

                    await Connection.ExecuteAsync(
                        "UPDATE AccountContact SET ContactPictureFileId = ?, Base64ContactPicture = NULL WHERE Address = ?",
                        fileId,
                        contact.Address).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to migrate Base64ContactPicture for contact {Address}.", contact.Address);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to migrate contact pictures from base64 to file system.");
        }
    }

    private string BuildFilePath(Guid fileId) => Path.Combine(_contactPicturesFolder, $"{fileId}.jpg");
}
