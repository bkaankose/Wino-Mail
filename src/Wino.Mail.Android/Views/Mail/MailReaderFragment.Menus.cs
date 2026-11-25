using Android.Content;
using Android.Views;
using AndroidX.Core.Content;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Reader;
using Wino.Mail.Android.Rendering;
using Wino.Mail.ViewModels;
using Wino.Platform.Android.Services;
using Wino.Renderer;
using Wino.Renderer.Android;
using Wino.Renderer.Primitives;
using Wino.Renderer.Selection;

namespace Wino.Mail.Android.Views.Mail;

/// <summary>
/// The reader's menus over the message body (Windows MailRenderingPage.Insights and MailView_ContextMenuRequested): the
/// date menu on a tap on an underlined date, and the long-press menu for dates, links, pictures, tables and text.
/// </summary>
public sealed partial class MailReaderFragment
{
    private float DisplayDensity() => Context?.Resources?.DisplayMetrics?.Density ?? 1f;

    private void ShowBodyMenu(MailView view, ContextMenuRequest request)
    {
        if (ViewModel is not { } viewModel || view.Parent is not FrameLayout host || Context is not { } context) return;

        var targets = request.Targets.Count > 0 ? request.Targets : view.Reader?.GetContextTargets(request.ViewPoint) ?? Array.Empty<ContextTarget>();
        var rows = new List<EngineMenuEntry?>();

        if (FindDate(targets) is { } date)
        {
            rows.AddRange(DateRows(date, viewModel));
            rows.Add(null);
        }
        if (request.HasSelection)
            rows.Add(new EngineMenuEntry(Translator.Buttons_Copy, WinoIconGlyph.Copy, () => view.CopySelection(), AutomationId: "MailRenderingCopy"));
        if (request.LinkUrl is { Length: > 0 } url)
            rows.Add(new EngineMenuEntry(Translator.Reader_ContextMenu_CopyLink, WinoIconGlyph.Link, () => Observe(viewModel.CopyLinkCommand.ExecuteAsync(url)), AutomationId: "MailRenderingCopyLink"));
        if (request.OnImage && view.Reader is { } reader && reader.TryGetImageAt(request.ViewPoint, out var image) && !image.Bytes.IsEmpty)
            rows.Add(new EngineMenuEntry(Translator.Reader_ContextMenu_CopyImage, WinoIconGlyph.Image, () => Observe(CopyImageAsync(context, image)), AutomationId: "MailRenderingCopyImage"));
        foreach (var target in targets)
        {
            if (target.Kind != ContextTargetKind.Table) continue;
            rows.Add(new EngineMenuEntry(Translator.Reader_ContextMenu_CopyTable, WinoIconGlyph.Table, () => CopyTable(view, target, TableTextFormat.TabSeparated), AutomationId: "MailRenderingCopyTable"));
            rows.Add(new EngineMenuEntry(Translator.Reader_ContextMenu_CopyTableAsCsv, WinoIconGlyph.Document, () => CopyTable(view, target, TableTextFormat.Csv), AutomationId: "MailRenderingCopyTableAsCsv"));
            break;
        }
        if (rows.Count > 0) rows.Add(null);
        rows.Add(new EngineMenuEntry(Translator.Reader_ContextMenu_SelectAll, WinoIconGlyph.SelectAll, () => view.SelectAll(), AutomationId: "MailRenderingSelectAll"));

        EngineContextMenu.Show(host, request.ViewPoint, DisplayDensity(), _dark, rows);
    }

    private MailDetectedDate? FindDate(IReadOnlyList<ContextTarget> targets)
    {
        foreach (var target in targets)
        {
            if (target.Kind == ContextTargetKind.Date && _messageReader?.GetDate(target.Index) is { } date) return date;
        }

        return null;
    }

    private IEnumerable<EngineMenuEntry?> DateRows(MailDetectedDate date, MailRenderingPageViewModel viewModel)
    {
        var text = MailRenderingPageViewModel.FormatDetectedDate(date);
        yield return new EngineMenuEntry(text, WinoIconGlyph.Calendar, () => { }, IsEnabled: false, AutomationId: "MailRenderingDateHeader");
        yield return new EngineMenuEntry(Translator.Reader_Date_AddToCalendar, WinoIconGlyph.CalendarAdd,
            () => Observe(viewModel.AddDateToCalendarCommand.ExecuteAsync(date)), AutomationId: "MailRenderingDateAddToCalendar");
        yield return new EngineMenuEntry(Translator.Reader_Date_CreateTask, WinoIconGlyph.TaskList,
            () => Observe(viewModel.CreateTaskForDateCommand.ExecuteAsync(date)), AutomationId: "MailRenderingDateCreateTask");
        yield return null;
        yield return new EngineMenuEntry(Translator.Reader_Date_Copy, WinoIconGlyph.Copy,
            () => Observe(viewModel.CopyClipboardCommand.ExecuteAsync(text)), AutomationId: "MailRenderingDateCopy");
    }

    /// <summary>A tap that is not a link opens the menu of an underlined date (Windows MailView_Tapped).</summary>
    private void ShowDateMenuAt(MotionEvent tap)
    {
        if (_mailView is not { } view || _messageReader is not { } reader || _disposed || ViewModel is not { } viewModel) return;
        var density = DisplayDensity();
        var point = new PxPoint(tap.GetX() / density, tap.GetY() / density);

        if (view.Reader?.HitTest(point).Link is not null || reader.GetDateAt(point.X, point.Y) is not { } date) return;
        if (view.Parent is not FrameLayout host) return;

        EngineContextMenu.Show(host, point, density, _dark, DateRows(date, viewModel).ToList());
    }

    private void CopyTable(MailView view, ContextTarget target, TableTextFormat format)
    {
        if (Context is not { } context || view.Reader?.GetTableContent(target, format) is not { } content || string.IsNullOrEmpty(content.PlainText)) return;

        var clip = format == TableTextFormat.TabSeparated && !string.IsNullOrEmpty(content.Html)
            ? ClipData.NewHtmlText("table", content.PlainText, content.Html)
            : ClipData.NewPlainText("table", content.PlainText);
        if (context.GetSystemService(Context.ClipboardService) is ClipboardManager clipboard) clipboard.PrimaryClip = clip;
    }

    /// <summary>
    /// Copies a picture as an image clip. The bytes go to the app's shared-files folder, the one the file provider
    /// serves, so another app can read the image from the clipboard.
    /// </summary>
    private static async Task CopyImageAsync(Context context, MailResource image)
    {
        var extension = Path.GetExtension(image.FileName ?? string.Empty);
        if (extension.Length == 0)
            extension = "." + (global::Android.Webkit.MimeTypeMap.Singleton?.GetExtensionFromMimeType(image.MediaType) ?? "img");

        var folder = Path.Combine(context.CacheDir?.AbsolutePath ?? throw new InvalidOperationException("The cache folder is unavailable."), "shared-files");
        var path = Path.Combine(folder, $"picture-{Guid.NewGuid():N}{extension}");
        await Task.Run(() =>
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(path, image.Bytes.ToArray());
        });

        var uri = FileProvider.GetUriForFile(context, AndroidExternalLauncher.GetFileProviderAuthority(context), new Java.IO.File(path))
            ?? throw new InvalidOperationException("The picture cannot be shared.");
        if (context.GetSystemService(Context.ClipboardService) is ClipboardManager clipboard)
            clipboard.PrimaryClip = ClipData.NewUri(context.ContentResolver, image.FileName ?? "picture", uri);
    }

    /// <summary>Reports a touch that lifts without moving far or for long: a tap, not a scroll, a selection or a long press.</summary>
    private sealed class TapListener(Action<MotionEvent> tapped, int slop, int timeout) : Java.Lang.Object, View.IOnTouchListener
    {
        private float _x;
        private float _y;
        private bool _tracking;

        public bool OnTouch(View? view, MotionEvent? e)
        {
            if (e is null) return false;

            switch (e.ActionMasked)
            {
                case MotionEventActions.Down:
                    _x = e.GetX();
                    _y = e.GetY();
                    _tracking = true;
                    break;
                case MotionEventActions.Move:
                    if (Math.Abs(e.GetX() - _x) > slop || Math.Abs(e.GetY() - _y) > slop) _tracking = false;
                    break;
                case MotionEventActions.Up:
                    if (_tracking && e.EventTime - e.DownTime < timeout) tapped(e);
                    _tracking = false;
                    break;
                case MotionEventActions.PointerDown:
                case MotionEventActions.Cancel:
                    _tracking = false;
                    break;
            }

            return false;
        }
    }
}
