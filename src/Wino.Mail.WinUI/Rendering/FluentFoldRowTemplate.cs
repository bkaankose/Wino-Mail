using Wino.Renderer;

namespace Wino.Mail.WinUI.Rendering;

/// <summary>
/// The reply rows of conversation folding in the reader, drawn as a WinUI Expander header: the card fill and stroke,
/// 4 px corners, a 48 px row and the chevron glyph of Segoe Fluent Icons. The colours are the Fluent theme resources
/// (CardBackgroundFillColorDefault, CardStrokeColorDefault, TextFillColorPrimary and TextFillColorSecondary) for the
/// message's light or dark theme; forced colours use the system colours.
/// </summary>
internal static class FluentFoldRowTemplate
{
    public static FoldRowTemplate Instance { get; } = new("""
        .row {
          display: flex;
          align-items: center;
          box-sizing: border-box;
          min-height: 48px;
          margin: 8px 0;
          padding: 0 0 0 16px;
          border: 1px solid rgba(0, 0, 0, 0.0578);
          border-radius: 4px;
          background: rgba(255, 255, 255, 0.7);
          font-family: "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif;
          font-size: 14px;
          color: rgba(0, 0, 0, 0.8956);
        }
        .text { flex: 1 1 auto; min-width: 0; overflow: hidden; white-space: nowrap; text-overflow: ellipsis; }
        .title { font-weight: 600; }
        .subtitle { margin-left: 8px; font-size: 12px; color: rgba(0, 0, 0, 0.6063); }
        .chevron {
          flex: none;
          display: block;
          width: 32px;
          height: 32px;
          margin: 0 8px 0 20px;
          line-height: 32px;
          text-align: center;
          font-family: "Segoe Fluent Icons", "Segoe MDL2 Assets";
          font-size: 12px;
          color: rgba(0, 0, 0, 0.6063);
        }
        .row.folded .chevron::before { content: "\E70D"; }
        .row.expanded .chevron::before { content: "\E70E"; }
        @media (prefers-color-scheme: dark) {
          .row { border-color: rgba(0, 0, 0, 0.1); background: rgba(255, 255, 255, 0.0512); color: #ffffff; }
          .subtitle, .chevron { color: rgba(255, 255, 255, 0.786); }
        }
        @media (forced-colors: active) {
          .row { border-color: CanvasText; background: Canvas; color: CanvasText; }
          .subtitle, .chevron { color: CanvasText; }
        }
        """);
}
