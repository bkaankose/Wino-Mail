using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;
using Wino.Core.Domain.Models.Common;

namespace Wino.Core.UWP.Extensions;

public static class StorageFileExtensions
{
    public static async Task<SharedFile> ToSharedFileAsync(this Windows.Storage.StorageFile storageFile)
    {
        var content = await storageFile.ToByteArrayAsync();

        return new SharedFile(storageFile.Path, content);
    }

    public static async Task<byte[]> ToByteArrayAsync(this StorageFile file)
    {
        if (file == null)
            throw new ArgumentNullException(nameof(file));

        using (var stream = await file.OpenReadAsync())
        using (var memoryStream = new MemoryStream())
        {
            await stream.AsStreamForRead().CopyToAsync(memoryStream);
            return memoryStream.ToArray();
        }
    }
}
