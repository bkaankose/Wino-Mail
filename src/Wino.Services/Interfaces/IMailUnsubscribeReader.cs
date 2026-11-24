using MimeKit;
using Wino.Core.Domain.Models.Reader;

namespace Wino.Services.Interfaces;

/// <summary>
/// Reads how to unsubscribe from a message. A head that renders with the mail engine registers the engine's reader;
/// the others keep <see cref="HeaderUnsubscribeReader"/>.
/// </summary>
public interface IMailUnsubscribeReader
{
    /// <summary>
    /// Null when the message offers no usable method.
    /// </summary>
    UnsubscribeInfo Read(MimeMessage message);
}
