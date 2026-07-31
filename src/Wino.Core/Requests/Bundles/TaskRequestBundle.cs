using System;
using System.Threading.Tasks;
using MailKit.Net.Imap;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Requests.Bundles;

public class ImapRequest
{
    public Func<IImapClient, IRequestBase, Task> IntegratorTask { get; }
    public IRequestBase Request { get; }
    public bool RequiresConnectedClient { get; }

    public ImapRequest(Func<IImapClient, IRequestBase, Task> integratorTask, IRequestBase request, bool requiresConnectedClient = true)
    {
        IntegratorTask = integratorTask;
        Request = request;
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
