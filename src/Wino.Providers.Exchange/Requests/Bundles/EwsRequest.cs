#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Requests.Bundles;

/// <summary>
/// One on-premises Exchange operation. The EWS synchronizer runs it against a freshly authenticated
/// ExchangeService, the MAPI over HTTP synchronizer on a borrowed MAPI session.
/// </summary>
public class EwsRequest
{
    public Func<IRequestBase, CancellationToken, Task> IntegratorTask { get; }
    public IRequestBase Request { get; }

    public EwsRequest(Func<IRequestBase, CancellationToken, Task> integratorTask, IRequestBase request)
    {
        IntegratorTask = integratorTask;
        Request = request;
    }
}

public record EwsRequestBundle(EwsRequest NativeRequest, IRequestBase Request, IUIChangeRequest UIChangeRequest) : IRequestBundle<EwsRequest>
{
    public string BundleId { get; set; } = Guid.NewGuid().ToString();
}
