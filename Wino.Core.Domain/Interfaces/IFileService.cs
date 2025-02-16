using System.IO;
using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces
{
    public interface IFileService
    {
        Task<string> CopyFileAsync(string sourceFilePath, string destinationFolderPath);
        Task<Stream> GetFileStreamAsync(string folderPath, string fileName);
        Task<string> GetFileContentByApplicationUriAsync(string resourcePath);

        /// <summary>
        /// Zips all existing logs and saves to picked destination folder.
        /// </summary>
        /// <param name="logsFolder">Folder path where logs are stored.</param>
        /// <param name="destinationFolder">Target path to save the archive file.</param>
        /// <returns>True if zip is created with at least one item, false if logs are not found.</returns>
        Task<bool> SaveLogsToFolderAsync(string logsFolder, string destinationFolder);
    }
}
