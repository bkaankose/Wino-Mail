using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Reader;

/// <summary>
/// Rendering options for mail.
/// </summary>
public class MailRenderingOptions
{
    private const bool DefaultRenderPlaintextLinksValue = true;

    public RemoteContentMode RemoteContent { get; set; } = RemoteContentMode.LoadEverything;

    /// <summary>Quoted earlier messages and signatures are shown folded behind a button.</summary>
    public bool FoldEarlierMessages { get; set; }

    /// <summary>With <see cref="FoldEarlierMessages"/>, each earlier message folds behind its own row.</summary>
    public bool FoldConversation { get; set; }

    /// <summary>The sender's structured data (bookings, orders) is read after the message is shown.</summary>
    public bool FindStructuredData { get; set; }

    /// <summary>Dates in the text are found after the message is shown.</summary>
    public bool FindDates { get; set; }
    public bool RenderPlaintextLinks { get; set; } = DefaultRenderPlaintextLinksValue;

    // Read-only bridge for the platform readers until they map RemoteContent themselves; remove it then.
    public bool LoadImages => RemoteContent != RemoteContentMode.BlockAll;

    // HtmlDocument.Load call is redundant if all the settings are in default values.
    // Therefore we will purify the HTML only if needed.

    public bool IsPurifyingNeeded() => RemoteContent != RemoteContentMode.LoadEverything;
}
