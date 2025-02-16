using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces;

public interface IClipboardService
{
    Task CopyClipboardAsync(string text);
}
