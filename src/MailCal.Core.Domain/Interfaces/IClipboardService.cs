using System.Threading;
using System.Threading.Tasks;
using MailCal.Core.Domain.Models.Platform;

namespace MailCal.Core.Domain.Interfaces;

public interface IClipboardService
{
    Task<PlatformOperationResult> CopyTextAsync(string text, CancellationToken cancellationToken = default);
}
