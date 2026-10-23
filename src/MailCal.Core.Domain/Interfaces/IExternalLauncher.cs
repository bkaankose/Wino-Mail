using System;
using System.Threading;
using System.Threading.Tasks;
using MailCal.Core.Domain.Models.Platform;

namespace MailCal.Core.Domain.Interfaces;

public interface IExternalLauncher
{
    Task<PlatformOperationResult> LaunchFileAsync(string path, CancellationToken cancellationToken = default);
    Task<PlatformOperationResult> LaunchUriAsync(Uri uri, CancellationToken cancellationToken = default);
}
