using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.CardDav;

namespace Wino.Core.Domain.Interfaces;

public interface IDavMultistatusReader
{
    Task<DavMultistatus> ReadAsync(Stream stream, CancellationToken cancellationToken = default);
}
