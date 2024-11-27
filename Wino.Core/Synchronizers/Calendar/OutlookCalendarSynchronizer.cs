using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Kiota.Abstractions;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Synchronizers.Calendar
{
    public class OutlookCalendarSynchronizer : BaseSynchronizer<RequestInformation>
    {
        public OutlookCalendarSynchronizer(MailAccount account) : base(account)
        {
        }

        public override Task ExecuteNativeRequestsAsync(List<IRequestBundle<RequestInformation>> batchedRequests, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }
    }
}
