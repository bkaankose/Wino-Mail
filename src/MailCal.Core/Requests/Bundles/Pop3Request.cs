using System;
using System.Threading;
using System.Threading.Tasks;
using MailCal.Core.Domain.Interfaces;

namespace MailCal.Core.Requests.Bundles;

public sealed record Pop3Request(Func<CancellationToken, Task> ExecuteAsync);

public sealed record Pop3RequestBundle(
    Pop3Request NativeRequest,
    IRequestBase Request,
    IUIChangeRequest UIChangeRequest) : IRequestBundle<Pop3Request>
{
    public string BundleId { get; set; } = Guid.NewGuid().ToString();
}
