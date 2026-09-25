using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Stores contact and account profile pictures as JPEG files in the application local folder
/// instead of as base64 in SQLite. Files are named by a Guid and resolved through ms-appdata URIs.
/// </summary>
public interface IPictureStorageService
{
    /// <summary>
    /// Returns the full file path for the given file ID, or null if the file does not exist on disk.
    /// </summary>
    string GetPicturePath(PictureKind kind, Guid fileId);

    /// <summary>
    /// Returns the package-local URI for a stored picture, or null if the file does not exist.
    /// </summary>
    Uri GetPictureUri(PictureKind kind, Guid fileId);

    /// <summary>
    /// Writes the image to disk and returns the new file ID. Account profile pictures are normalized first.
    /// When <paramref name="replacedFileId"/> is given, that file is deleted after the new one is written.
    /// </summary>
    Task<Guid> SavePictureAsync(PictureKind kind, byte[] imageData, Guid? replacedFileId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the picture file for the given file ID if it exists.
    /// </summary>
    Task DeletePictureAsync(PictureKind kind, Guid fileId);
}
