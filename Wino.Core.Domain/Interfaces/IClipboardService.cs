using System.Threading.Tasks;

namespace Wino.Domain.Interfaces
{
    public interface IClipboardService
    {
        Task CopyClipboardAsync(string text);
    }
}
