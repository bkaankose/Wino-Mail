using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Platform;

namespace Wino.Core.Domain.Interfaces;

public interface IClipboardService
{
    Task<PlatformOperationResult> CopyTextAsync(string text, CancellationToken cancellationToken = default);
}
