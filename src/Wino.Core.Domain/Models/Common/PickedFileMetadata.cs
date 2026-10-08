using System.IO;

namespace Wino.Core.Domain.Models.Common;

public record PickedFileMetadata(string FullFilePath, long Size)
{
    public string FileName => Path.GetFileName(FullFilePath);
    public string FileExtension => Path.GetExtension(FullFilePath)?.ToLowerInvariant() ?? string.Empty;
}
