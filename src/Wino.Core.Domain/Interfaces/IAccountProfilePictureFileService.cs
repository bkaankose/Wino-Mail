using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Stores normalized, account-owned profile pictures in the application local folder.
/// </summary>
public interface IAccountProfilePictureFileService
{
    string GetProfilePicturePath(Guid fileId);
    Uri GetProfilePictureUri(Guid fileId);
    Task<Guid> SaveProfilePictureAsync(byte[] imageData, Guid? replacedFileId = null, CancellationToken cancellationToken = default);
    Task DeleteProfilePictureAsync(Guid fileId);
}
