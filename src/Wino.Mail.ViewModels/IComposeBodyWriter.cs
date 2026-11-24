#nullable enable
using System.Threading;
using System.Threading.Tasks;
using MimeKit;

namespace Wino.Mail.ViewModels;

/// <summary>
/// A composer that writes the outgoing body itself. A head sets it on <see cref="ComposePageViewModel.BodyWriter"/>;
/// heads without one hand over an HTML string through <see cref="ComposePageViewModel.GetHTMLBodyFunction"/>.
/// </summary>
public interface IComposeBodyWriter
{
    /// <summary>
    /// Sets <see cref="BodyBuilder.HtmlBody"/>, <see cref="BodyBuilder.TextBody"/> and
    /// <see cref="BodyBuilder.LinkedResources"/> from the editor's content. The linked resources are empty on
    /// entry; attachments belong to the ViewModel and stay untouched. Called on the UI thread.
    /// </summary>
    Task ApplyAsync(BodyBuilder builder, CancellationToken cancellationToken);
}

/// <summary>
/// The draft a composer that reads MIME itself opens.
/// </summary>
/// <param name="MimePath">The folder or file the draft was read from, or null when it has none.</param>
/// <param name="ShouldBlockRemoteContent">True when the draft's remote content must not load.</param>
public sealed record ComposeDraftRenderRequest(MimeMessage Draft, string? MimePath, bool ShouldBlockRemoteContent);
