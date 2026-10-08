using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using MailKit.Net.Imap;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Requests.Bundles;

public class ImapRequest
{
    public Func<IImapClient, IRequestBase, Task> IntegratorTask { get; }
    private readonly Func<IImapClient, IRequestBase, CancellationToken, Task> cancellableTask;

    public Task ExecuteAsync(IImapClient client, IRequestBase request, CancellationToken cancellationToken)
        => cancellableTask != null ? cancellableTask(client, request, cancellationToken) : IntegratorTask(client, request);

    public ImapRequest(Func<IImapClient, IRequestBase, CancellationToken, Task> action, IRequestBase request, bool requiresConnectedClient = true)
    {
        cancellableTask = action;
        IntegratorTask = (client, item) => action(client, item, CancellationToken.None);
        Request = request;
        QueuedRequests = new[] { request };
        RequiresConnectedClient = requiresConnectedClient;
    }

    public IReadOnlyList<IRequestBase> QueuedRequests { get; set; }
    public IRequestBase Request { get; }
    public bool RequiresConnectedClient { get; }

    public ImapRequest(Func<IImapClient, IRequestBase, Task> integratorTask, IRequestBase request, bool requiresConnectedClient = true)
    {
        IntegratorTask = integratorTask;
        Request = request;
        QueuedRequests = new[] { request };
        RequiresConnectedClient = requiresConnectedClient;
    }
}

public class ImapRequest<TRequestBaseType> : ImapRequest where TRequestBaseType : IRequestBase
{
    public ImapRequest(Func<IImapClient, TRequestBaseType, Task> integratorTask, TRequestBaseType request, bool requiresConnectedClient = true)
        : base((client, request) => integratorTask(client, (TRequestBaseType)request), request, requiresConnectedClient)
    {
    }
}

public record ImapRequestBundle(ImapRequest NativeRequest, IRequestBase Request, IUIChangeRequest UIChangeRequest) : IRequestBundle<ImapRequest>
{
    public string BundleId { get; set; } = Guid.NewGuid().ToString();
}
