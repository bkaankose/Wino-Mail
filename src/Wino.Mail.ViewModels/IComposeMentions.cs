#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;

namespace Wino.Mail.ViewModels;

/// <summary>The "@query" typed at the composer's caret. The anchor is the box of the "@" in the editor view's coordinates.</summary>
public sealed record ComposeMentionQuery(string Text, double AnchorX, double AnchorY, double AnchorWidth, double AnchorHeight);

public enum ComposeMentionKey
{
    Up,
    Down,
    Accept,
    Cancel
}

/// <summary>
/// A composer that reports "@query" at its caret and writes the person picked into the message. A head sets it on
/// <see cref="ComposePageViewModel.Mentions"/>; without one, typing "@" suggests nobody. The composer holds the user's
/// mention settings; the events are raised on the UI thread.
/// </summary>
public interface IComposeMentions
{
    /// <summary>The query started, changed, or ended (null).</summary>
    event EventHandler<ComposeMentionQuery?>? MentionQueryChanged;

    /// <summary>Up, Down, Enter or Tab (Accept) and Escape (Cancel) while the list is open; the composer does not act on them.</summary>
    event EventHandler<ComposeMentionKey>? MentionKeyPressed;

    Task SetMentionSettingsAsync(bool isEnabled, ComposerMentionFormat format, ComposerMentionRecipient addTo, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the query at the caret with the person, as a link when there is an address. Returns the recipient field
    /// the person belongs in (None for a person without an address), or null when nothing was inserted.
    /// </summary>
    Task<ComposerMentionRecipient?> InsertMentionAsync(string name, string? address, CancellationToken cancellationToken = default);

    /// <summary>Hides the query until its "@" is deleted or the caret leaves it.</summary>
    Task DismissMentionAsync(CancellationToken cancellationToken = default);

    /// <summary>While the list is open the composer hands the list keys to <see cref="MentionKeyPressed"/>.</summary>
    Task SetMentionListOpenAsync(bool isOpen, CancellationToken cancellationToken = default);
}
