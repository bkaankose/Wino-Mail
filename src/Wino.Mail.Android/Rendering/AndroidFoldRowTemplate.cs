using Wino.Renderer;

namespace Wino.Mail.Android.Rendering;

/// <summary>
/// The reply rows of conversation folding in the reader, drawn as a Material 3 expandable list item: an outlined
/// surface with 12 dp corners, a 56 dp row with the title (16 sp, medium) and the supporting text (14 sp) and the
/// engine's chevron at the end. The colours are the Material 3 baseline roles for the message's light or dark theme;
/// forced colours use the system colours.
/// </summary>
internal static class AndroidFoldRowTemplate
{
    public static FoldRowTemplate Instance { get; } = new("""
        .row {
          display: flex;
          align-items: center;
          box-sizing: border-box;
          min-height: 56px;
          margin: 8px 0;
          padding: 0 0 0 16px;
          border: 1px solid #CAC4D0;
          border-radius: 12px;
          background: #FFFBFE;
          font-family: Roboto, "sans-serif", system-ui, sans-serif;
          font-size: 16px;
          color: #1C1B1F;
        }
        .text { flex: 1 1 auto; min-width: 0; overflow: hidden; white-space: nowrap; text-overflow: ellipsis; }
        .title { font-weight: 500; }
        .subtitle { margin-left: 8px; font-size: 14px; color: #49454F; }
        .chevron {
          flex: none;
          display: block;
          width: 24px;
          height: 24px;
          margin: 0 12px 0 12px;
          color: #49454F;
        }
        @media (prefers-color-scheme: dark) {
          .row { border-color: #49454F; background: #1C1B1F; color: #E6E1E5; }
          .subtitle, .chevron { color: #CAC4D0; }
        }
        @media (forced-colors: active) {
          .row { border-color: CanvasText; background: Canvas; color: CanvasText; }
          .subtitle, .chevron { color: CanvasText; }
        }
        """);
}
