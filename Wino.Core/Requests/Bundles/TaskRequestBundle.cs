using System;
using System.Threading.Tasks;
using MailKit.Net.Imap;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Requests.Bundles;

public class ImapRequest
{
    public Func<IImapClient, IRequestBase, Task> IntegratorTask { get; }
    public IRequestBase Request { get; }

    public ImapRequest(Func<IImapClient, IRequestBase, Task> integratorTask, IRequestBase request)
    {
        IntegratorTask = integratorTask;
        Request = request;
    }
}

public class ImapRequest<TRequestBaseType> : ImapRequest where TRequestBaseType : IRequestBase
{
    public ImapRequest(Func<IImapClient, TRequestBaseType, Task> integratorTask, TRequestBaseType request)
        : base((client, request) => integratorTask(client, (TRequestBaseType)request), request)
    {
    }
}

public record ImapRequestBundle(ImapRequest NativeRequest, IRequestBase Request, IUIChangeRequest UIChangeRequest) : IExecutableRequest<ImapRequest>
{
    // IRequestBase implementation
    public object GroupingKey() => Request?.GroupingKey() ?? string.Empty;
    public int ResynchronizationDelay => Request?.ResynchronizationDelay ?? 0;

    // IUIChangeRequest implementation
    public void ApplyUIChanges() => UIChangeRequest?.ApplyUIChanges();
    public void RevertUIChanges() => UIChangeRequest?.RevertUIChanges();

    // IExecutableRequest implementation
    Task<object> IExecutableRequest.PrepareNativeRequestAsync(IRequestExecutionContext context)
    {
        ApplyUIChanges();
        return Task.FromResult<object>(NativeRequest);
    }

    Task<ImapRequest> IExecutableRequest<ImapRequest>.PrepareNativeRequestAsync(IRequestExecutionContext context)
    {
        ApplyUIChanges();
        return Task.FromResult(NativeRequest);
    }

    public Task HandleResponseAsync(object response, IRequestExecutionContext context)
    {
        return Task.CompletedTask;
    }

    public Task HandleFailureAsync(Exception error, IRequestExecutionContext context)
    {
        RevertUIChanges();
        return Task.CompletedTask;
    }
}
