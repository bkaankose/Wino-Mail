using System;
using System.Threading.Tasks;
using MailKit.Net.Imap;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Requests.Bundles
{
    public class ImapRequest
    {
        public Func<ImapClient, IRequestBase, Task> IntegratorTask { get; }
        public IRequestBase Request { get; }

        public ImapRequest(Func<ImapClient, IRequestBase, Task> integratorTask, IRequestBase request)
        {
            IntegratorTask = integratorTask;
            Request = request;
        }
    }

    public class ImapRequest<TRequestBaseType> : ImapRequest where TRequestBaseType : IRequestBase
    {
        public ImapRequest(Func<ImapClient, TRequestBaseType, Task> integratorTask, TRequestBaseType request)
            : base((client, request) => integratorTask(client, (TRequestBaseType)request), request)
        {
        }
    }

    public record ImapRequestBundle(ImapRequest NativeRequest, IRequestBase Request, IUIChangeRequest UIChangeRequest) : IRequestBundle<ImapRequest>
    {
        public string BundleId { get; set; } = Guid.NewGuid().ToString();
    }
}
