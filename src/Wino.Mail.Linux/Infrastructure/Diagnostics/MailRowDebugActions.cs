#if DEBUG
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.Core.MailRow;
using Wino.Mail.Controls.Linux.MailList;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// Debug actions of the mail row: <c>debug-mailrow-pref</c> <c>as</c> <c>[name, value]</c> sets a real preference
/// (<c>density</c> compact|medium|spacious, <c>preview</c> on|off, <c>pictures</c> on|off, <c>nickname</c>
/// none|left|right); <c>debug-mailrow-gallery</c> <c>s</c> (report path, optionally <c>#Density</c> to scroll to the end of that group, where the tile rows are) opens the <c>Mail row gallery</c> window with
/// every row state in the three densities and writes the mean <c>Apply</c> time.
/// </summary>
internal static class MailRowDebugActions
{
    private const string WindowTitle = "Mail row gallery";

    private static Adw.Window? _window;
    private static readonly List<WinoMailRow> _rows = [];

    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        var pref = Gio.SimpleAction.New("debug-mailrow-pref", GLib.VariantType.New("as"));
        pref.OnActivate += (_, args) => Run("debug-mailrow-pref", () =>
        {
            var arguments = args.Parameter?.GetStrv(out var _) ?? [];
            if (arguments.Length < 2) throw new ArgumentException("Expected [name, value].");
            if (services()?.GetService<IPreferencesService>() is not { } preferences) throw new InvalidOperationException("The app has not started.");
            var value = arguments[1];
            switch (arguments[0])
            {
                case "density":
                    preferences.MailItemDisplayMode = value switch
                    {
                        "compact" => MailListDisplayMode.Compact,
                        "medium" => MailListDisplayMode.Medium,
                        "spacious" => MailListDisplayMode.Spacious,
                        _ => throw new ArgumentException($"'{value}' is not a density."),
                    };
                    break;
                case "preview": preferences.IsShowPreviewEnabled = OnOff(value); break;
                case "pictures": preferences.IsShowSenderPicturesEnabled = OnOff(value); break;
                case "nickname":
                    preferences.AccountNicknamePosition = value switch
                    {
                        "none" => AccountNicknamePosition.None,
                        "left" => AccountNicknamePosition.Left,
                        "right" => AccountNicknamePosition.Right,
                        _ => throw new ArgumentException($"'{value}' is not a nickname position."),
                    };
                    break;
                default: throw new ArgumentException($"'{arguments[0]}' is not a row preference.");
            }
            Serilog.Log.Information("Debug action: mail row preference {Name} = {Value}", arguments[0], value);
        });
        application.AddAction(pref);

        var gallery = Gio.SimpleAction.New("debug-mailrow-gallery", GLib.VariantType.New("s"));
        gallery.OnActivate += (_, args) => Run("debug-mailrow-gallery", () =>
        {
            var parameter = args.Parameter?.GetString(out var _) ?? string.Empty;
            var split = parameter.LastIndexOf('#');
            var path = split >= 0 ? parameter[..split] : parameter;
            ShowGallery(application);
            if (split >= 0) ScrollGalleryTo(parameter[(split + 1)..]);
            var report = Measure();
            if (path.Length > 0) DebugActions.WriteText(path, report);
            Serilog.Log.Information("Debug action: mail row gallery {Report}", report.Trim().Replace('\n', ' '));
        });
        application.AddAction(gallery);
    }

    private static bool OnOff(string value) => value switch
    {
        "on" => true,
        "off" => false,
        _ => throw new ArgumentException($"'{value}' is not on or off."),
    };

    private static void Run(string name, Action action)
    {
        try { action(); }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action {Name} failed.", name); }
    }

    private static WinoMailRowModel[] Models(WinoMailRowDensity density)
    {
        var plain = new WinoMailRowModel
        {
            Density = density,
            Sender = "Ada Lovelace",
            SenderAddress = "ada@example.org",
            Subject = "Notes on the analytical engine",
            Preview = "The engine might compose elaborate pieces of music of any degree of complexity.",
            DateText = "09:41",
            AccessibilityText = "Ada Lovelace, Notes on the analytical engine, 09:41",
        };
        return
        [
            plain,
            plain with { Sender = "Grace Hopper", SenderAddress = "grace@example.org", Subject = "Unread: bug found in relay 70", IsUnread = true, AccessibilityText = "Grace Hopper, Unread" },
            plain with { Sender = "Alan Turing", SenderAddress = "alan@example.org", Subject = "Flagged with attachment, pinned", IsFlagged = true, HasAttachments = true, IsPinned = true },
            plain with { Sender = string.Empty, SenderAddress = "draft@example.org", Subject = "Draft with an empty sender", IsDraft = true },
            plain with { Sender = "Busy Row", Subject = "Synchronizing this message", IsBusy = true },
            plain with { Kind = WinoMailRowKind.ThreadHead, ThreadCount = 3, Sender = "Thread Head", Subject = "Collapsed conversation" },
            plain with { Kind = WinoMailRowKind.ThreadHead, ThreadCount = 3, IsThreadExpanded = true, Sender = "Thread Head", Subject = "Expanded conversation" },
            plain with { Kind = WinoMailRowKind.ThreadChild, Sender = "Thread Child", Subject = "Re: Expanded conversation" },
            plain with { Sender = "Left Nickname", AccountNickname = "Work", NicknamePosition = AccountNicknamePosition.Left, AccountColorHex = "#E74C3C" },
            plain with { Sender = "Right Nickname", AccountNickname = "Home", NicknamePosition = AccountNicknamePosition.Right, AccountColorHex = "#2ECC71" },
            plain with
            {
                Sender = "Bartholomew Maximilian Featherstonehaugh-Cholmondeley of the Very Long Name Society",
                Subject = "An extraordinarily long subject line that must ellipsise instead of widening the mail list pane at all",
                Preview = "A preview that goes on\nacross line breaks and on and on and on and on and on and on and on and on and on and on and on.",
            },
            plain with { Sender = "Three Categories", Subject = "Category dots before the flag", IsFlagged = true, HasAttachments = true, CategoryDotColors = ["#E74C3C", "#3498DB", null], CategoryNames = "Red, Blue, No colour" },
            plain with { Sender = "Six Tiles", Subject = "Six tiles wrap", Tiles = [Red, Blue, Green, Amber, Plain, Purple] },
            plain with { Sender = "Eight Tiles", Subject = "Eight tiles, two not shown", Tiles = [Red, Blue, Green, Amber, Plain, Purple, Red with { Text = "Seventh" }, Blue with { Text = "Eighth" }] },
            plain with
            {
                Sender = "Intelligence",
                                Subject = "Priority, two smart labels and a coloured chip",
                Tiles =
                [
                    new WinoMailRowTile("High priority", WinoIcons.Glyph(WinoIconGlyph.Warning), null, null, true, true, "Priority: high"),
                    new WinoMailRowTile("Newsletter", WinoIcons.Glyph(WinoIconGlyph.Flag), null, null, true),
                    new WinoMailRowTile("No glyph", null, null, null, true),
                    new WinoMailRowTile("A & <B>", null, "#DBEAFE", "#1E40AF", false),
                ],
            },
            plain with { Sender = "Selection Mode", Subject = "Checked in select mode" },
        ];
    }

    private static readonly WinoMailRowTile Red = new("Red", null, "#FEE2E2", "#991B1B", false);
    private static readonly WinoMailRowTile Blue = new("Blue", null, "#DBEAFE", "#1E40AF", false);
    private static readonly WinoMailRowTile Green = new("A rather long category name that ellipsizes", null, "#DCFCE7", "#166534", false);
    private static readonly WinoMailRowTile Amber = new("Amber", null, "#F59E0B", null, false);
    private static readonly WinoMailRowTile Plain = new("No colour", null, null, null, false);
    private static readonly WinoMailRowTile Purple = new("Purple", null, "#6D28D9", "#FFFFFF", false);

    private static void ShowGallery(Adw.Application application)
    {
        if (_window is { } previous)
        {
            _window = null;
            previous.Close();
        }
        foreach (var row in _rows) row.Dispose();
        _rows.Clear();

        var content = Gtk.Box.New(Gtk.Orientation.Vertical, 12);
        content.SetMarginTop(12);
        content.SetMarginBottom(12);
        foreach (var (density, mode) in (ValueTuple<WinoMailRowDensity, MailListDisplayMode>[])[
                     (WinoMailRowDensity.Compact, MailListDisplayMode.Compact),
                     (WinoMailRowDensity.Medium, MailListDisplayMode.Medium),
                     (WinoMailRowDensity.Spacious, MailListDisplayMode.Spacious)])
        {
            var header = new WinoMailGroupHeader { Title = density.ToString() };
            content.Append(header.Root);
            var list = Gtk.ListBox.New();
            list.AddCssClass(WinoCssClass.MailList);
            WinoStyle.SetDensity(list, mode);
            list.SetSelectionMode(Gtk.SelectionMode.Single);
            var models = Models(density);
            for (var index = 0; index < models.Length; index++)
            {
                var row = new WinoMailRow();
                _rows.Add(row);
                row.Apply(models[index]);
                if (index == models.Length - 1)
                {
                    row.IsSelectionMode = true;
                    row.IsChecked = true;
                }
                list.Append(row.Root);
            }
            if (list.GetRowAtIndex(2) is { } selected) list.SelectRow(selected);
            content.Append(list);
        }

        var scroll = Gtk.ScrolledWindow.New();
        scroll.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        scroll.SetChild(content);
        scroll.SetVexpand(true);

        var window = Adw.Window.New();
        window.SetApplication(application);
        window.SetTitle(WindowTitle);
        window.SetDefaultSize(720, 900);
        var toolbar = Adw.ToolbarView.New();
        toolbar.AddTopBar(Adw.HeaderBar.New());
        toolbar.SetContent(scroll);
        window.SetContent(toolbar);
        window.OnCloseRequest += (_, _) =>
        {
            if (ReferenceEquals(_window, window)) _window = null;
            return false;
        };
        _window = window;
        window.Present();
    }

    private static void ScrollGalleryTo(string density)
    {
        MainLoop.Delay(400, () =>
        {
            if (_window?.GetContent() is not Adw.ToolbarView toolbar || toolbar.GetContent() is not Gtk.ScrolledWindow scroll) return;
            if (scroll.GetChild() is not { } viewport || viewport.GetFirstChild() is not { } content) return;
            for (var child = content.GetFirstChild(); child is not null; child = child.GetNextSibling())
            {
                if (child.GetFirstChild() is Gtk.Label label && string.Equals(label.GetText(), density, StringComparison.OrdinalIgnoreCase)
                    && child.GetNextSibling() is { } list && list.ComputeBounds(content, out var bounds))
                {
                    var adjustment = scroll.GetVadjustment();
                    adjustment.SetValue(bounds.GetY() + bounds.GetHeight() - adjustment.GetPageSize());
                    return;
                }
            }
        });
    }

    private static string Measure()
    {
        var models = Models(WinoMailRowDensity.Medium);
        var first = models[1];
        var second = models[2];
        using var row = new WinoMailRow();
        const int count = 10_000;
        var started = Stopwatch.GetTimestamp();
        for (var index = 0; index < count; index++) row.Apply(index % 2 == 0 ? first : second);
        var elapsed = Stopwatch.GetElapsedTime(started);
        var mean = elapsed.TotalMilliseconds * 1000 / count;
        return string.Create(CultureInfo.InvariantCulture, $"apply_us={mean:F2}\nrows={_rows.Count}\n");
    }
}
#endif
