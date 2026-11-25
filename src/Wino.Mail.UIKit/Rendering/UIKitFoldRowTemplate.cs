using Wino.Renderer;

namespace Wino.Mail.UIKit.Rendering;

/// <summary>
/// The reply rows of conversation folding in the reader, drawn as a UIKit disclosure row: a 44 pt inset-grouped row on
/// the system 10 pt radius, the body text size, and a right chevron that points down when the earlier message is open.
/// The colours are literal values for the message's light or dark theme; high contrast uses the system colours.
/// </summary>
internal static class UIKitFoldRowTemplate
{
    public static FoldRowTemplate Instance { get; } = new("""
        .row {
          display: flex;
          align-items: center;
          box-sizing: border-box;
          min-height: 44px;
          margin: 8px 0;
          padding: 0 12px 0 16px;
          border: 0;
          border-radius: 10px;
          background: #ffffff;
          font-family: -apple-system, system-ui, sans-serif;
          font-size: 16px;
          color: #000000;
        }
        .text { flex: 1 1 auto; min-width: 0; overflow: hidden; white-space: nowrap; text-overflow: ellipsis; }
        .title { font-weight: 600; }
        .subtitle { margin-left: 6px; font-size: 13px; color: rgba(60, 60, 67, 0.6); }
        .chevron {
          flex: none;
          display: block;
          width: 16px;
          height: 16px;
          margin: 0 0 0 10px;
          line-height: 16px;
          text-align: center;
          font-family: -apple-system, system-ui, sans-serif;
          font-size: 13px;
          font-weight: 600;
          color: rgba(60, 60, 67, 0.3);
        }
        .row.folded .chevron::before { content: "\203A"; }
        .row.expanded .chevron::before { content: "\2304"; }
        @media (prefers-color-scheme: dark) {
          .row { background: #1c1c1e; color: #ffffff; }
          .subtitle { color: rgba(235, 235, 245, 0.6); }
          .chevron { color: rgba(235, 235, 245, 0.3); }
        }
        @media (forced-colors: active) {
          .row { border: 1px solid CanvasText; background: Canvas; color: CanvasText; }
          .subtitle, .chevron { color: CanvasText; }
        }
        """);
}
