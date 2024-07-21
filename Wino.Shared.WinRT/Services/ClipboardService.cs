using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Wino.Domain.Interfaces;

namespace Wino.Shared.WinRT.Services
{
    public class ClipboardService : IClipboardService
    {
        public Task CopyClipboardAsync(string text)
        {
            var package = new DataPackage();
            package.SetText(text);

            Clipboard.SetContent(package);

            return Task.CompletedTask;
        }
    }
}
