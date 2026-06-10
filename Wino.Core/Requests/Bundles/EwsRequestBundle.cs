using System;
using System.Threading.Tasks;
using Microsoft.Exchange.WebServices.Data;
using Wino.Core.Domain.Interfaces;
// EWS defines its own Task item type; alias bare `Task` to the TPL Task.
using Task = System.Threading.Tasks.Task;

namespace Wino.Core.Requests.Bundles;

/// <summary>A single EWS operation expressed over a connected <see cref="ExchangeService"/>.</summary>
public class EwsRequest
{
    public Func<ExchangeService, IRequestBase, Task> IntegratorTask { get; }
    public IRequestBase Request { get; }

    public EwsRequest(Func<ExchangeService, IRequestBase, Task> integratorTask, IRequestBase request)
    {
        IntegratorTask = integratorTask;
        Request = request;
    }
}

public class EwsRequest<TRequestBaseType> : EwsRequest where TRequestBaseType : IRequestBase
{
    public EwsRequest(Func<ExchangeService, TRequestBaseType, Task> integratorTask, TRequestBaseType request)
        : base((service, request) => integratorTask(service, (TRequestBaseType)request), request)
    {
    }
}

public record EwsRequestBundle(EwsRequest NativeRequest, IRequestBase Request, IUIChangeRequest UIChangeRequest) : IRequestBundle<EwsRequest>
{
    public string BundleId { get; set; } = Guid.NewGuid().ToString();
}
