using System;
using System.Threading.Tasks;
using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Synchronizers.Calendar
{
    public class OutlookCalendarSynchronizer : BaseCalendarSynchronizer<RequestInformation, Event>
    {
        public OutlookCalendarSynchronizer(MailAccount account)
        {
            Account = account;
        }

        public MailAccount Account { get; }

        public override Task<Event> CreateCalendarEventAsync(RequestInformation request)
        {
            throw new NotImplementedException();
        }
    }
}
