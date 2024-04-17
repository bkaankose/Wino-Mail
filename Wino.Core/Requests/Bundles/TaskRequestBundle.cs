using System;
using System.Threading.Tasks;
using MailKit.Net.Imap;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Requests.Bundles
{
    //public abstract record TaskRequestBundleBase()
    //{
    //    public abstract Task ExecuteAsync(ImapClient executorImapClient);
    //}

    //public record TaskRequestBundle(Func<ImapClient, Task> NativeRequest) : TaskRequestBundleBase
    //{
    //    public override async Task ExecuteAsync(ImapClient executorImapClient) => await NativeRequest(executorImapClient).ConfigureAwait(false);
    //}

    public record ImapRequest(Func<ImapClient, Task> IntegratorTask, IRequestBase Request) { }

    public record ImapRequestBundle(ImapRequest NativeRequest, IRequestBase Request) : IRequestBundle<ImapRequest>
    {
        public string BundleId { get; set; } = Guid.NewGuid().ToString();
    }
}
