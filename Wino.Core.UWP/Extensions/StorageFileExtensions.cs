using System.Threading.Tasks;
using Microsoft.Toolkit.Uwp.Helpers;
using Wino.Core.Domain.Models.Common;

namespace Wino.Core.UWP.Extensions
{
    public static class StorageFileExtensions
    {
        public static async Task<SharedFile> ToSharedFileAsync(this Windows.Storage.StorageFile storageFile)
        {
            var content = await storageFile.ReadBytesAsync();

            return new SharedFile(storageFile.Path, content);
        }
    }
}
