#if DEBUG
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.Core;
using Wino.Mail.Controls.Core.AccountIcon;
using Wino.Mail.Controls.Linux.Common;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// Debug actions of the contact picture, the account icon and the sender picture provider, each with a string
/// parameter: <c>debug-avatars</c> (a report path; opens the <c>Avatars</c> gallery), <c>debug-sender-picture</c> (an
/// address: looks it up and logs every later change) and <c>debug-sender-pictures</c> (<c>on</c> or <c>off</c>: the
/// shared preference).
/// </summary>
internal static class AvatarDebugActions
{
    private const string WindowTitle = "Avatars";
    private const string OnePixelPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

    private static readonly (string? Name, string? Address)[] Identities =
    [
        ("Ada Lovelace", "ada@example.org"),
        ("grace", "grace.hopper@example.org"),
        (null, "noreply@example.net"),
        (null, null),
    ];

    private static Adw.Window? _window;
    private static string? _liveAddress;
    private static readonly HashSet<string> Watched = new(StringComparer.Ordinal);
    private static EventHandler<string?>? _watch;

    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        Add(application, "debug-avatars", path =>
        {
            if (services()?.GetService<LinuxSenderPictureProvider>() is not { } provider) throw new InvalidOperationException("The app has not started.");
            WriteReport(path);
            ShowGallery(application, provider);
        });
        Add(application, "debug-sender-picture", address =>
        {
            if (services()?.GetService<LinuxSenderPictureProvider>() is not { } provider) throw new InvalidOperationException("The app has not started.");
            _liveAddress = address;
            if (LinuxSenderPictureProvider.Normalize(address) is { } key) Watched.Add(key);
            if (_watch is null)
            {
                _watch = static (_, changed) =>
                {
                    if (changed is null || Watched.Contains(changed))
                        Serilog.Log.Information("Debug action: sender picture changed {Address}: {Size}", changed ?? "(all)", changed is null ? "reset" : Size(Provider?.Get(changed)));
                };
                provider.PictureChanged += _watch;
                Provider = provider;
            }
            var state = !provider.IsEnabled ? "disabled" : provider.Get(address) is { } texture ? "cached " + Size(texture) : "pending";
            Serilog.Log.Information("Sender picture {Address}: {State}", address, state);
        });
        Add(application, "debug-sender-pictures", value =>
        {
            if (services()?.GetService<IPreferencesService>() is not { } preferences) throw new InvalidOperationException("The app has not started.");
            preferences.IsShowSenderPicturesEnabled = value switch
            {
                "on" => true,
                "off" => false,
                _ => throw new ArgumentException($"'{value}' is not on or off."),
            };
        });
    }

    private static LinuxSenderPictureProvider? Provider { get; set; }

    private static string Size(Gdk.Texture? texture) => texture is null ? "none" : $"{texture.GetWidth()}x{texture.GetHeight()}";

    private static void WriteReport(string path)
    {
        var text = new StringBuilder();
        foreach (var (name, address) in Identities)
        {
            text.Append(name ?? "(null)").Append(" | ").Append(address ?? "(null)")
                .Append(" | initials ").Append(ContactPictureAppearance.Initials(string.IsNullOrWhiteSpace(name) ? address : name))
                .Append(" | index ").Append(ContactPictureAppearance.PaletteIndex(address ?? name))
                .Append(" | ").AppendLine(ContactPictureAppearance.PaletteColorHex(address ?? name));
        }
        var temporary = path + ".partial";
        File.WriteAllText(temporary, text.ToString());
        File.Move(temporary, path, overwrite: true);
    }

    private static void ShowGallery(Adw.Application application, LinuxSenderPictureProvider provider)
    {
        if (_window is not null)
        {
            _window.Present();
            return;
        }

        var content = Gtk.Box.New(Gtk.Orientation.Vertical, 12);
        content.SetMarginTop(16);
        content.SetMarginBottom(16);
        content.SetMarginStart(16);
        content.SetMarginEnd(16);

        foreach (var size in (int[])[WinoContactPicture.ReaderSize, WinoContactPicture.RowSize, WinoContactPicture.ThreadChildSize])
        {
            var row = Row();
            foreach (var (name, address) in Identities)
            {
                var picture = new WinoContactPicture(size);
                picture.SetIdentity(name, address);
                row.Append(picture.Root);
            }
            content.Append(row);
        }

        var photos = Row();
        var bytes = new WinoContactPicture(WinoContactPicture.ReaderSize);
        bytes.SetIdentity("Photo", "photo@example.org");
        bytes.Picture = Textures.FromBytes(Convert.FromBase64String(OnePixelPng));
        photos.Append(bytes.Root);
        var square = new WinoContactPicture(WinoContactPicture.ReaderSize) { IsSquare = true };
        square.SetIdentity("Square Corner", "square@example.org");
        photos.Append(square.Root);
        var badged = new WinoContactPicture(WinoContactPicture.ReaderSize);
        badged.SetIdentity("Badge Holder", "badge@example.org");
        badged.SetBadge(WinoIcons.Glyph(WinoIconGlyph.Google));
        photos.Append(badged.Root);
        content.Append(photos);

        foreach (var tint in (string?[])[null, "#3498DB"])
        {
            foreach (var size in (int[])[28, 22])
            {
                var row = Row();
                foreach (var kind in Enum.GetValues<AccountIconProvider>())
                    row.Append(new WinoAccountIcon(size) { Account = new AccountIconInfo(kind, AccountColorHex: tint), AccessibleName = kind.ToString() }.Root);
                content.Append(row);
            }
        }

        var special = Row();
        special.Append(new WinoAccountIcon(28) { Account = new AccountIconInfo(AccountIconProvider.Imap, ProviderGlyph: WinoIcons.Glyph(WinoIconGlyph.Yahoo)), AccessibleName = "Yahoo glyph" }.Root);
        special.Append(new WinoAccountIcon(28) { Account = new AccountIconInfo(AccountIconProvider.Google, ProfilePicturePath: "/nonexistent/wino/profile.png"), AccessibleName = "Missing picture" }.Root);
        var stack = new WinoAccountIconStack(28);
        stack.SetAccounts([new AccountIconInfo(AccountIconProvider.Microsoft), new AccountIconInfo(AccountIconProvider.Google), new AccountIconInfo(AccountIconProvider.Imap)]);
        special.Append(stack.Root);
        content.Append(special);

        var live = new WinoContactPicture(WinoContactPicture.ReaderSize);
        var liveRow = Row();
        liveRow.Append(live.Root);
        content.Append(liveRow);
        void Refresh()
        {
            live.SetIdentity(null, _liveAddress);
            live.Picture = provider.Get(_liveAddress);
        }
        Refresh();
        EventHandler<string?> changed = (_, address) =>
        {
            if (address is not null && address != LinuxSenderPictureProvider.Normalize(_liveAddress)) return;
            Serilog.Log.Information("Debug action: live avatar refreshed for {Address}", address ?? "(all)");
            Refresh();
        };
        provider.PictureChanged += changed;

        var window = Adw.Window.New();
        window.SetApplication(application);
        window.SetTitle(WindowTitle);
        window.SetDefaultSize(800, 520);
        var toolbar = Adw.ToolbarView.New();
        toolbar.AddTopBar(Adw.HeaderBar.New());
        toolbar.SetContent(content);
        window.SetContent(toolbar);
        window.OnCloseRequest += (_, _) =>
        {
            provider.PictureChanged -= changed;
            _window = null;
            return false;
        };
        _window = window;
        window.Present();
    }

    private static Gtk.Box Row()
    {
        var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
        row.SetHalign(Gtk.Align.Start);
        return row;
    }

    private static void Add(Adw.Application application, string name, Action<string> run)
    {
        var action = Gio.SimpleAction.New(name, GLib.VariantType.New("s"));
        action.OnActivate += (_, args) =>
        {
            var value = args.Parameter?.GetString(out var _) ?? string.Empty;
            try
            {
                run(value);
                Serilog.Log.Information("Debug action {Name} {Value} done.", name, value);
            }
            catch (Exception exception) { Serilog.Log.Error(exception, "Debug action {Name} failed.", name); }
        };
        application.AddAction(action);
    }
}
#endif
