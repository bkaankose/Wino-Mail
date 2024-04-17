using System.IO;
using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces
{
    public interface IFileService
    {
        Task<string> CopyFileAsync(string sourceFilePath, string destinationFolderPath);
        Task<Stream> GetFileStreamAsync(string folderPath, string fileName);
        Task<string> GetFileContentByApplicationUriAsync(string resourcePath);
    }
}
