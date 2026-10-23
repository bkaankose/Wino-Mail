using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MailCal.Core.Domain.Models.CardDav;

namespace MailCal.Core.Domain.Interfaces;

public interface IDavMultistatusReader
{
    Task<DavMultistatus> ReadAsync(Stream stream, CancellationToken cancellationToken = default);
}
