using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.UWP.Services
{
    public class FileService : IFileService
    {
        public async Task<string> CopyFileAsync(string sourceFilePath, string destinationFolderPath)
        {
            var fileName = Path.GetFileName(sourceFilePath);

            var sourceFileHandle = await StorageFile.GetFileFromPathAsync(sourceFilePath);
            var destinationFolder = await StorageFolder.GetFolderFromPathAsync(destinationFolderPath);

            var copiedFile = await sourceFileHandle.CopyAsync(destinationFolder, fileName, NameCollisionOption.GenerateUniqueName);

            return copiedFile.Path;
        }

        public async Task<string> GetFileContentByApplicationUriAsync(string resourcePath)
        {
            var releaseNoteFile = await StorageFile.GetFileFromApplicationUriAsync(new Uri(resourcePath));

            return await FileIO.ReadTextAsync(releaseNoteFile);
        }

        public async Task<Stream> GetFileStreamAsync(string folderPath, string fileName)
        {
            var folder = await StorageFolder.GetFolderFromPathAsync(folderPath);
            var createdFile = await folder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);

            return await createdFile.OpenStreamForWriteAsync();
        }
    }
}
