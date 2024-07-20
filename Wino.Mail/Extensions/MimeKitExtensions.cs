using System.Threading.Tasks;
using Windows.Storage;
using Wino.Helpers;
using Wino.Mail.ViewModels.Data;

namespace Wino.Extensions
{
    public static class MimeKitExtensions
    {
        public static async Task<MailAttachmentViewModel> ToAttachmentViewModelAsync(this StorageFile storageFile)
        {
            if (storageFile == null) return null;

            var bytes = await storageFile.ReadBytesAsync();

            return new MailAttachmentViewModel(storageFile.Name, bytes);
        }
    }
}
