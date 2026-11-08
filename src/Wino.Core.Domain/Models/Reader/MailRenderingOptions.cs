using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Reader;

/// <summary>
/// Rendering options for mail.
/// </summary>
public class MailRenderingOptions
{
    private const bool DefaultLoadStylesValue = true;
    private const bool DefaultRenderPlaintextLinksValue = true;

    public RemoteContentMode RemoteContent { get; set; } = RemoteContentMode.LoadEverything;
    public bool LoadStyles { get; set; } = DefaultLoadStylesValue;
    public bool RenderPlaintextLinks { get; set; } = DefaultRenderPlaintextLinksValue;

    // Read-only bridge for the platform readers until they map RemoteContent themselves; remove it then.
    public bool LoadImages => RemoteContent != RemoteContentMode.BlockAll;

    // HtmlDocument.Load call is redundant if all the settings are in default values.
    // Therefore we will purify the HTML only if needed.

    public bool IsPurifyingNeeded() => RemoteContent != RemoteContentMode.LoadEverything || LoadStyles != DefaultLoadStylesValue;
}
