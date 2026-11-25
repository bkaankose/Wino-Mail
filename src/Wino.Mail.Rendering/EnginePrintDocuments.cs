using System.Globalization;
using Wino.Core.Domain;
using Wino.Renderer.Print;

namespace Wino.Mail.Rendering;

/// <summary>The print options every head lays a message out with: paper, margins, the header and the page numbers.</summary>
public static class EnginePrintDocuments
{
    private const float MarginPx = 96;

    // A print dialog replaces the paper with the printer's; this size is only the default it opens with and the paper
    // of a PDF export.
    public static PrintOptions CreateOptions(string? title, PrintHeader header)
        => new(RegionInfo.CurrentRegion.IsMetric ? PrintOptions.A4 : PrintOptions.Letter, PrintOptions.UniformMargins(MarginPx), 1, Backgrounds: false)
        {
            Header = header,
            MarginBoxes = PrintOptions.StandardMarginBoxes(title, header.Date, Translator.Reader_Print_PageWord, Translator.Reader_Print_OfWord)
        };
}
