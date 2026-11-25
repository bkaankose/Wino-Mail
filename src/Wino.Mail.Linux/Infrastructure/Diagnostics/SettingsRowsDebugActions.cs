#if DEBUG
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Presentation.Bindings;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// A gallery of every settings row kind bound to a plain model, to check <see cref="SettingsBinder"/> without a real page.
/// <c>debug-settings-gallery</c> opens or presents the window "Settings rows gallery", <c>debug-settings-gallery-close</c>
/// hides it, <c>debug-settings-gallery-state</c> <c>s</c> writes the model and its write log to a file, and
/// <c>debug-settings-gallery-set</c> <c>as</c> (<c>[property, value]</c>) changes the model from outside.
/// </summary>
internal static class SettingsRowsDebugActions
{
    private static Gallery? _gallery;

    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        Add(application, "debug-settings-gallery", null, _ =>
        {
            _gallery ??= new Gallery(application, Required<IDispatcher>(services));
            _gallery.Window.Present();
        });
        Add(application, "debug-settings-gallery-close", null, _ => _gallery?.Window.Close());
        Add(application, "debug-settings-gallery-state", "s", parameter =>
        {
            var path = parameter?.GetString(out _) ?? throw new ArgumentException("Pass a path.");
            var gallery = _gallery ?? throw new InvalidOperationException("The gallery has not been opened.");
            DebugActions.WriteText(path, gallery.Model.Describe());
            Serilog.Log.Information("Debug action: settings gallery state written to {Path}.", path);
        });
        Add(application, "debug-settings-gallery-set", "as", parameter =>
        {
            var arguments = parameter?.GetStrv(out _) ?? [];
            if (arguments.Length < 2) throw new ArgumentException("Pass [property, value].");
            var gallery = _gallery ?? throw new InvalidOperationException("The gallery has not been opened.");
            gallery.Model.Set(arguments[0], arguments[1]);
            Serilog.Log.Information("Debug action: settings gallery {Property} set to {Value}.", arguments[0], arguments[1]);
        });
    }

    private static T Required<T>(Func<IServiceProvider?> services) where T : notnull
        => (services() ?? throw new InvalidOperationException("The app has not started.")).GetRequiredService<T>();

    private static void Add(Adw.Application application, string name, string? parameterType, Action<GLib.Variant?> handler)
    {
        var action = Gio.SimpleAction.New(name, parameterType is null ? null : GLib.VariantType.New(parameterType));
        action.OnActivate += (_, args) =>
        {
            try { handler(args.Parameter); }
            catch (Exception exception) { Serilog.Log.Error(exception, "Debug action {Name} failed.", name); }
        };
        application.AddAction(action);
    }

    private sealed record GalleryItem(string Name);

    private sealed class Gallery
    {
        private static readonly GalleryItem[][] ItemSets =
        [
            [new("Alpha"), new("Beta"), new("Gamma")],
            [new("Delta"), new("Beta"), new("Epsilon")],
            [new("Zeta"), new("Eta")],
        ];

        private readonly SettingsBinder _settings;
        private readonly WinoSettingsGroup _collectionGroup = new("Collection");
        private SettingsBinder? _collectionBinder;
        private int _itemSet;

        public Gallery(Adw.Application application, IDispatcher dispatcher)
        {
            _settings = new SettingsBinder(new BindingScope(), dispatcher,
                exception => Serilog.Log.Error(exception, "Settings rows gallery binding failed."));
            foreach (var item in ItemSets[0]) Model.Items.Add(item);
            Model.Item = Model.Items[1];

            var page = new WinoSettingsPage();
            Build(page);

            var header = Adw.HeaderBar.New();
            var toolbar = Adw.ToolbarView.New();
            toolbar.AddTopBar(header);
            toolbar.SetContent(page.Root);

            Window = Adw.Window.New();
            Window.SetTitle("Settings rows gallery");
            Window.SetDefaultSize(760, 720);
            Window.SetHideOnClose(true);
            Window.SetContent(toolbar);
            application.AddWindow(Window);
        }

        public Adw.Window Window { get; }

        public GalleryModel Model { get; } = new();

        private void Build(WinoSettingsPage page)
        {
            var s = _settings;
            var m = Model;

            var expander = WinoSettingsRows.Expander("Expander with switch", "Children follow the header switch", WinoIconGlyph.Settings, true,
                s.Enabled(s.Switch(m, "Child switch", null, WinoIconGlyph.None, nameof(GalleryModel.Child), static x => x.Child, static (x, v) => x.Child = v),
                    m, nameof(GalleryModel.ExpanderOn), static x => x.ExpanderOn),
                s.Enabled(s.Number(m, "Child number", null, WinoIconGlyph.None, nameof(GalleryModel.Count), static x => x.Count, static (x, v) => x.Count = v, 0, 20),
                    m, nameof(GalleryModel.ExpanderOn), static x => x.ExpanderOn));
            WinoSettingsRows.Suffix(expander, s.SwitchSuffix(m, "Expander switch", nameof(GalleryModel.ExpanderOn), static x => x.ExpanderOn, static (x, v) => x.ExpanderOn = v));

            page.AddGroup("Toggles", null,
                s.Switch(m, "Flag", "A plain two-way switch; enables the commands", WinoIconGlyph.CheckmarkCircle,
                    nameof(GalleryModel.Flag), static x => x.Flag, static (x, v) => x.Flag = v),
                s.CommandSwitch(m, "Command switch", "Runs a command that flips the value", WinoIconGlyph.Sync,
                    nameof(GalleryModel.Commanded), static x => x.Commanded, m.ToggleCommandedCommand),
                expander,
                WinoSettingsRows.Action("A & B <c>", "Markup stays text: x & y <z>, and a long description that has to wrap onto a second line instead of being cut off at the end of the row.", WinoIconGlyph.Info));

            var replace = s.Button("Replace items", ReplaceItems);
            var itemRow = s.Choice(m, "Item choice", "Rebuilt when the items change", WinoIconGlyph.Folder,
                static x => x.Items, static item => item.Name,
                nameof(GalleryModel.Item), static x => x.Item, static (x, v) => x.Item = v, itemsCollection: m.Items);
            var pair = WinoSettingsRows.Action("Two choices", "Index and item drop-downs side by side");
            WinoSettingsRows.Suffix(pair, s.ChoiceSuffix(m, "Index drop-down", (string[])["One", "Two", "Three"],
                nameof(GalleryModel.Index), static x => x.Index, static (x, v) => x.Index = v));
            WinoSettingsRows.Suffix(pair, s.ChoiceSuffix(m, "Item drop-down", static x => x.Items, static item => item.Name,
                nameof(GalleryModel.Item), static x => x.Item, static (x, v) => x.Item = v, itemsCollection: m.Items));
            var replaceRow = WinoSettingsRows.Action("Items", null);
            WinoSettingsRows.Suffix(replaceRow, replace);
            page.AddGroup("Choices", null,
                s.Choice(m, "Index choice", null, WinoIconGlyph.Mail, (string[])["One", "Two", "Three"],
                    nameof(GalleryModel.Index), static x => x.Index, static (x, v) => x.Index = v),
                itemRow, replaceRow, pair);

            var numbers = WinoSettingsRows.Action("Switch and number", "Two suffix controls");
            WinoSettingsRows.Suffix(numbers, s.SwitchSuffix(m, "Small switch", nameof(GalleryModel.Small), static x => x.Small, static (x, v) => x.Small = v));
            WinoSettingsRows.Suffix(numbers, s.NumberSuffix(m, "Small number", nameof(GalleryModel.Count), static x => x.Count, static (x, v) => x.Count = v, 0, 20));
            var time = WinoSettingsRows.Action("Time", "Hours and minutes", WinoIconGlyph.Clock);
            WinoSettingsRows.Suffix(time, s.TimeSuffix(m, "Time", nameof(GalleryModel.Time), static x => x.Time, static (x, v) => x.Time = v));
            page.AddGroup("Numbers", null,
                s.Number(m, "Count", "Clamped to 1..10 by the model", WinoIconGlyph.Add,
                    nameof(GalleryModel.Count), static x => x.Count, static (x, v) => x.Count = v, 0, 20),
                numbers, time);

            var textRow = WinoSettingsRows.Action("Text suffix", null, WinoIconGlyph.Edit);
            WinoSettingsRows.Suffix(textRow, s.TextSuffix(m, "Text suffix", "Placeholder", nameof(GalleryModel.Name), static x => x.Name, static (x, v) => x.Name = v));
            textRow.AddSuffix(s.LabelSuffix(m, nameof(GalleryModel.Name), static x => x.Name.Length));
            page.AddGroup("Text", null,
                s.Invalid(s.Text(m, "Name", nameof(GalleryModel.Name), static x => x.Name, static (x, v) => x.Name = v),
                    m, nameof(GalleryModel.Name), static x => string.IsNullOrWhiteSpace(x.Name)),
                textRow);

            var buttons = WinoSettingsRows.Action("Two buttons", null);
            WinoSettingsRows.Suffix(buttons, s.Button("Increment", m.IncrementCommand, suggested: true));
            WinoSettingsRows.Suffix(buttons, s.Button("Reset", () => m.Count = 1, icon: WinoIconGlyph.Delete));
            var destructive = WinoSettingsRows.Button("Destructive button row", destructive: true);
            page.AddGroup("Commands", null,
                s.Command("Command", "Runs Increment", WinoIconGlyph.Add, m.IncrementCommand),
                s.Command("External command", "Opens something outside", WinoIconGlyph.Link, m.IncrementCommand, external: true),
                s.Navigation("Navigation", "Writes to the log", WinoIconGlyph.Settings, () => m.Append("Navigation")),
                buttons, destructive);

            page.AddGroup("State", null,
                s.Switch(m, "Enable driven group", null, WinoIconGlyph.None, nameof(GalleryModel.IsEnabled), static x => x.IsEnabled, static (x, v) => x.IsEnabled = v),
                s.Switch(m, "Show driven row", null, WinoIconGlyph.None, nameof(GalleryModel.IsVisible), static x => x.IsVisible, static (x, v) => x.IsVisible = v));
            var driven = page.AddGroup("Driven", "Enabled and visible follow the State group",
                WinoSettingsRows.Action("Always here", "Insensitive with the group"),
                s.Visible(WinoSettingsRows.Action("Driven row", "Hidden by the switch above"), m, nameof(GalleryModel.IsVisible), static x => x.IsVisible));
            s.Enabled(driven.Root, m, nameof(GalleryModel.IsEnabled), static x => x.IsEnabled);

            var banner = page.AddBanner("A banner revealed by a switch & shown as text");
            s.Bind(m, nameof(GalleryModel.ShowBanner), static x => x.ShowBanner, banner.SetRevealed);
            var bar = Gtk.ActionBar.New();
            bar.PackStart(s.Button("Discard", () => m.Append("Discard")));
            bar.PackEnd(s.Button("Apply", () => m.Append("Apply"), suggested: true));
            page.SetBottomBar(bar);
            page.AddGroup("Custom & free-form", "A custom row with a caption",
                WinoSettingsRows.Custom(WinoSettingsRows.Caption("Free-form content in a row that is not activatable. It wraps like a description."), "Caption"),
                s.Switch(m, "Show banner", null, WinoIconGlyph.Alert, nameof(GalleryModel.ShowBanner), static x => x.ShowBanner, static (x, v) => x.ShowBanner = v));

            page.Add(_collectionGroup);
            s.Collection(m.Items, RebuildCollection);
        }

        private void RebuildCollection()
        {
            _collectionBinder?.Scope.Dispose();
            _collectionBinder = _settings.Child();
            _collectionGroup.Clear();
            foreach (var item in Model.Items)
                _collectionGroup.Add(_collectionBinder.Navigation(item.Name, "Selects this item", WinoIconGlyph.None, () => Model.Item = item));
        }

        private void ReplaceItems()
        {
            _itemSet = (_itemSet + 1) % ItemSets.Length;
            Model.Items.Clear();
            foreach (var item in ItemSets[_itemSet]) Model.Items.Add(item);
            Model.Append($"Items replaced with set {_itemSet}");
        }
    }

    private sealed class GalleryModel : ObservableObject
    {
        private readonly List<string> _log = [];
        private bool _flag;
        private bool _commanded;
        private bool _expanderOn = true;
        private bool _child;
        private int _index;
        private GalleryItem? _item;
        private bool _small;
        private int _count = 1;
        private TimeSpan _time = new(8, 30, 0);
        private string _name = "Wino";
        private bool _isEnabled = true;
        private bool _isVisible = true;
        private bool _showBanner;

        public ObservableCollection<GalleryItem> Items { get; } = [];

        public bool Flag
        {
            get => _flag;
            set
            {
                if (!Write(ref _flag, value)) return;
                IncrementCommand.NotifyCanExecuteChanged();
                ToggleCommandedCommand.NotifyCanExecuteChanged();
            }
        }

        public bool Commanded { get => _commanded; set => Write(ref _commanded, value); }
        public bool ExpanderOn { get => _expanderOn; set => Write(ref _expanderOn, value); }
        public bool Child { get => _child; set => Write(ref _child, value); }
        public int Index { get => _index; set => Write(ref _index, value); }
        public GalleryItem? Item { get => _item; set => Write(ref _item, value); }
        public bool Small { get => _small; set => Write(ref _small, value); }
        public int Count { get => _count; set => Write(ref _count, Math.Clamp(value, 1, 10)); }
        public TimeSpan Time { get => _time; set => Write(ref _time, value); }
        public string Name { get => _name; set => Write(ref _name, value); }
        public bool IsEnabled { get => _isEnabled; set => Write(ref _isEnabled, value); }
        public bool IsVisible { get => _isVisible; set => Write(ref _isVisible, value); }
        public bool ShowBanner { get => _showBanner; set => Write(ref _showBanner, value); }

        public GalleryModel()
        {
            IncrementCommand = new RelayCommand(() =>
            {
                Append("Increment");
                Count++;
            }, () => Flag);
            ToggleCommandedCommand = new RelayCommand(() =>
            {
                Append("ToggleCommanded");
                Commanded = !Commanded;
            }, () => Flag);
        }

        public RelayCommand IncrementCommand { get; }

        public RelayCommand ToggleCommandedCommand { get; }

        public void Append(string entry) => _log.Add(entry);

        public void Set(string property, string value)
        {
            switch (property)
            {
                case nameof(Flag): Flag = bool.Parse(value); break;
                case nameof(Commanded): Commanded = bool.Parse(value); break;
                case nameof(ExpanderOn): ExpanderOn = bool.Parse(value); break;
                case nameof(Index): Index = int.Parse(value, CultureInfo.InvariantCulture); break;
                case nameof(Item): Item = Items.FirstOrDefault(item => item.Name == value) ?? new GalleryItem(value); break;
                case nameof(Count): Count = int.Parse(value, CultureInfo.InvariantCulture); break;
                case nameof(Time): Time = TimeSpan.Parse(value, CultureInfo.InvariantCulture); break;
                case nameof(Name): Name = value; break;
                case nameof(IsEnabled): IsEnabled = bool.Parse(value); break;
                case nameof(IsVisible): IsVisible = bool.Parse(value); break;
                case nameof(ShowBanner): ShowBanner = bool.Parse(value); break;
                default: throw new ArgumentException($"The gallery has no property '{property}'.");
            }
        }

        public string Describe()
        {
            var text = new StringBuilder();
            text.Append("Flag=").Append(Flag).AppendLine();
            text.Append("Commanded=").Append(Commanded).AppendLine();
            text.Append("ExpanderOn=").Append(ExpanderOn).AppendLine();
            text.Append("Child=").Append(Child).AppendLine();
            text.Append("Index=").Append(Index).AppendLine();
            text.Append("Item=").Append(Item?.Name ?? "-").AppendLine();
            text.Append("Items=").Append(string.Join(",", Items.Select(item => item.Name))).AppendLine();
            text.Append("Small=").Append(Small).AppendLine();
            text.Append("Count=").Append(Count).AppendLine();
            text.Append("Time=").Append(Time.ToString("hh\\:mm", CultureInfo.InvariantCulture)).AppendLine();
            text.Append("Name=").Append(Name).AppendLine();
            text.Append("IsEnabled=").Append(IsEnabled).AppendLine();
            text.Append("IsVisible=").Append(IsVisible).AppendLine();
            text.Append("ShowBanner=").Append(ShowBanner).AppendLine();
            text.Append("IncrementCanExecute=").Append(IncrementCommand.CanExecute(null)).AppendLine();
            text.Append("Log=").Append(string.Join("; ", _log)).AppendLine();
            return text.ToString();
        }

        private bool Write<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? property = null)
        {
            if (!SetProperty(ref field, value, property)) return false;
            _log.Add(string.Create(CultureInfo.InvariantCulture, $"{property}={value}"));
            return true;
        }
    }
}
#endif
