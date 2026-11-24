#nullable enable
using System.Threading;
using System.Threading.Tasks;
using MimeKit;
using Wino.Core.Domain.Models.Reader;
using Wino.Services.Models.Reader;

namespace Wino.Mail.ViewModels;

/// <summary>
/// What a head that renders MIME itself needs to show one message.
/// </summary>
/// <param name="MimePath">The folder or file the message was read from, or null when it has none.</param>
/// <param name="Options">The user's rendering options; <see cref="MailRenderingOptions.RemoteContent"/> already holds the effective mode for this message.</param>
public sealed record MailMessageRenderRequest(MimeMessage Message, string? MimePath, MailRenderingOptions Options, bool IsReaderView);

/// <summary>
/// A reader that takes the parsed message instead of prepared HTML. A head sets it on
/// <see cref="MailRenderingPageViewModel.MessageReader"/>; heads without one keep the HTML path.
/// </summary>
public interface IMailMessageReader
{
    /// <summary>
    /// Shows the message and returns what the reader page needs from it: attachments, unsubscribe information,
    /// signatures, the encryption flag, blocked resources, the remote image count, the accessible text and the
    /// options it rendered with. <see cref="MailRenderModel.RenderHtml"/> may be empty. Called on the UI thread.
    /// </summary>
    Task<MailRenderModel> RenderAsync(MailMessageRenderRequest request, CancellationToken cancellationToken);

    /// <summary>Removes the shown message. Called on the UI thread.</summary>
    Task ClearAsync();
}
