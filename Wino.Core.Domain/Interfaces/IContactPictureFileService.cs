using System;
using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Manages contact picture files stored on disk instead of as base64 in SQLite,
/// eliminating DB bloat and enabling native WIC hardware-accelerated image loading.
/// </summary>
public interface IContactPictureFileService
{
    /// <summary>
    /// Returns the full file path for the given file ID, or null if the file does not exist on disk.
    /// </summary>
    string GetContactPicturePath(Guid fileId);

    /// <summary>
    /// Saves raw image bytes to disk and returns the new file ID.
    /// </summary>
    Task<Guid> SaveContactPictureAsync(byte[] imageData);

    /// <summary>
    /// Deletes the picture file for the given file ID if it exists.
    /// </summary>
    Task DeleteContactPictureAsync(Guid fileId);

    /// <summary>
    /// One-time startup migration: reads AccountContact rows where Base64ContactPicture is set
    /// but ContactPictureFileId is null, writes the picture bytes to disk, updates the DB row,
    /// and clears the Base64ContactPicture column.
    /// </summary>
    Task MigrateBase64PicturesAsync();
}
