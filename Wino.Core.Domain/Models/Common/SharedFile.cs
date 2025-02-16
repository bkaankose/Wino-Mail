using System.IO;

namespace Wino.Core.Domain.Models.Common;

/// <summary>
/// Abstraction for StorageFile
/// </summary>
/// <param name="FullFilePath">Full path of the file.</param>
/// <param name="Data">Content</param>
public record SharedFile(string FullFilePath, byte[] Data)
{
    public string FileName => Path.GetFileName(FullFilePath);
}
