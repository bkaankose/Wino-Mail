using System.Threading.Tasks;
using MimeKit;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Models.Reader;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Decides which remote content mode applies to a message: Junk, a session override, a sender rule,
/// the contacts and recipients settings, then the default mode.
/// </summary>
public interface IRemoteContentPolicyService
{
    /// <param name="mail">The stored mail, or null for a message opened from a file.</param>
    /// <param name="message">The parsed message the sender is read from.</param>
    /// <param name="filePath">For a message without <paramref name="mail"/>, the file path its session override is keyed by.</param>
    Task<RemoteContentDecision> ResolveAsync(MailCopy? mail, MimeMessage message, string? filePath = null);
}
