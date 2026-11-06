using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Requests.Bundles;

public sealed record EasRequest(Func<CancellationToken, Task> ExecuteAsync);

public sealed record EasRequestBundle(EasRequest NativeRequest, IRequestBase Request, IUIChangeRequest UIChangeRequest) : IRequestBundle<EasRequest>
{
    public string BundleId { get; set; } = Guid.NewGuid().ToString();
}
