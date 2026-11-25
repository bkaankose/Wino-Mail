using Wino.Renderer;

namespace Wino.Mail.MacOS.Rendering;

/// <summary>
/// The reply rows of conversation folding in the reader, drawn as an AppKit disclosure group: a 28 pt row on the
/// system 6 pt radius, the system text sizes and a small triangle that points right when folded and down when open.
/// The colours are literal values for the message's light or dark theme; high contrast uses the system colours.
/// </summary>
internal static class AppKitFoldRowTemplate
{
    public static FoldRowTemplate Instance { get; } = new("""
        .row {
          display: flex;
          align-items: center;
          box-sizing: border-box;
          min-height: 28px;
          margin: 6px 0;
          padding: 0 10px 0 12px;
          border: 1px solid rgba(0, 0, 0, 0.1);
          border-radius: 6px;
          background: rgba(255, 255, 255, 0.6);
          font-family: "SF Pro Text", -apple-system, system-ui, sans-serif;
          font-size: 13px;
          color: rgba(0, 0, 0, 0.85);
        }
        .text { flex: 1 1 auto; min-width: 0; overflow: hidden; white-space: nowrap; text-overflow: ellipsis; }
        .title { font-weight: 600; }
        .subtitle { margin-left: 6px; font-size: 11px; color: rgba(0, 0, 0, 0.5); }
        .chevron {
          flex: none;
          display: block;
          width: 16px;
          height: 16px;
          margin: 0 4px 0 8px;
          line-height: 16px;
          text-align: center;
          font-family: "SF Pro Text", -apple-system, system-ui, sans-serif;
          font-size: 11px;
          color: rgba(0, 0, 0, 0.5);
        }
        .row.folded .chevron::before { content: "\25B6"; }
        .row.expanded .chevron::before { content: "\25BC"; }
        @media (prefers-color-scheme: dark) {
          .row { border-color: rgba(255, 255, 255, 0.1); background: rgba(255, 255, 255, 0.06); color: #ffffff; }
          .subtitle, .chevron { color: rgba(255, 255, 255, 0.55); }
        }
        @media (forced-colors: active) {
          .row { border-color: CanvasText; background: Canvas; color: CanvasText; }
          .subtitle, .chevron { color: CanvasText; }
        }
        """);
}
