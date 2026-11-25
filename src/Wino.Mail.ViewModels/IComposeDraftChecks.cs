#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wino.Mail.ViewModels;

public enum ComposeDraftCheckKind
{
    ImageDescription,
    ReadableText,
    MessageSize
}

/// <summary>
/// One problem a composer found in the user's own content. <paramref name="Measured"/> is the contrast ratio for
/// <see cref="ComposeDraftCheckKind.ReadableText"/> and the size in bytes for <see cref="ComposeDraftCheckKind.MessageSize"/>.
/// <paramref name="Token"/> belongs to the composer that reported the problem.
/// </summary>
public sealed record ComposeDraftIssue(ComposeDraftCheckKind Kind, double Measured, bool CanFix, object Token);

/// <summary>
/// A composer that can look over a draft before it is sent. A head sets it on <see cref="ComposePageViewModel.DraftChecks"/>;
/// without one, messages are sent unchecked. Quoted messages and signatures are never checked.
/// </summary>
public interface IComposeDraftChecks
{
    Task<IReadOnlyList<ComposeDraftIssue>> CheckAsync(bool imageDescriptions, bool readableText, long maximumBytes, CancellationToken cancellationToken = default);

    /// <summary>Selects the content of the problem and scrolls it into view.</summary>
    Task<bool> SelectAsync(ComposeDraftIssue issue, CancellationToken cancellationToken = default);

    /// <summary>Repairs a problem that reports <see cref="ComposeDraftIssue.CanFix"/>, as one undo step.</summary>
    Task<bool> FixAsync(ComposeDraftIssue issue, CancellationToken cancellationToken = default);

    /// <summary>Selects the picture of the problem and asks for its description.</summary>
    Task DescribeAsync(ComposeDraftIssue issue, CancellationToken cancellationToken = default);

    /// <summary>Marks the picture of the problem as decoration, which screen readers skip.</summary>
    Task<bool> MarkDecorativeAsync(ComposeDraftIssue issue, CancellationToken cancellationToken = default);
}
