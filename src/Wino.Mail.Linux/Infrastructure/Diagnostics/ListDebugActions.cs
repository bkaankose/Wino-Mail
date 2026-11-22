#if DEBUG
using System.Collections.ObjectModel;
using System.Text;
using Wino.Core.Domain.Interfaces;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Lists;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// Debug action of the list stack. <c>debug-list</c> <c>s</c>: opens a window titled <c>List test</c> with 50,000 rows in
/// five sections, runs the list self-check and writes one <c>PASS</c> or <c>FAIL</c> line per check to the path. After the
/// middle-change check it holds for ten seconds (log line "List self-check holding") so a driver can take screenshots.
/// </summary>
internal static class ListDebugActions
{
    private static bool _running;

    public static void Register(Adw.Application application)
    {
        var action = Gio.SimpleAction.New("debug-list", GLib.VariantType.New("s"));
        action.OnActivate += (_, args) =>
        {
            if (args.Parameter?.GetString(out var _) is not { Length: > 0 } path)
            {
                Serilog.Log.Error("Debug action debug-list needs a report path.");
                return;
            }
            if (_running)
            {
                Serilog.Log.Error("Debug action debug-list is already running.");
                return;
            }
            _running = true;
            RunAsync(application, path).ContinueWith(static task => Serilog.Log.Error(task.Exception, "Debug action debug-list failed."), TaskContinuationOptions.OnlyOnFaulted);
        };
        application.AddAction(action);
    }

    private static async Task RunAsync(Adw.Application application, string path)
    {
        var report = new StringBuilder();
        var errors = new List<Exception>();
        var cells = new List<WeakReference<DebugCell>>();
        var items = new List<WeakReference<GObject.Object>>();
        try
        {
            var scene = new Scene(application, report, errors, cells);
            await scene.RunAsync();
            scene.CollectItems(items, int.MaxValue);
            scene.Dispose();
            var sceneReference = new WeakReference<Scene>(scene);
            scene = null;

            var aliveCells = 0;
            var aliveItems = 0;
            var rounds = 0;
            for (var round = 0; round < 10; round++, rounds++)
            {
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                GC.WaitForPendingFinalizers();
                await Wait(200);
                aliveCells = cells.Count(static reference => reference.TryGetTarget(out _));
                aliveItems = items.Count(static reference => reference.TryGetTarget(out _));
                if (aliveCells + aliveItems == 0 && !sceneReference.TryGetTarget(out _) && round >= 2) break;
            }
            var tracked = WinoLiveObjects.Snapshot().Where(static entry => entry.Type == nameof(DebugCell)).Sum(static entry => entry.Count);
            var sceneAlive = sceneReference.TryGetTarget(out _);
            Line(report, "freed", !sceneAlive && aliveCells == 0 && aliveItems == 0 && tracked == 0,
                $"scene alive={sceneAlive} cells created={cells.Count} alive cells={aliveCells} alive items={aliveItems}/{items.Count} tracked DebugCell={tracked} rounds={rounds + 1}");
            Line(report, "errors", errors.Count == 0, errors.Count == 0 ? "none" : string.Join(" | ", errors.Select(static error => error.Message)));
        }
        catch (Exception exception)
        {
            Serilog.Log.Error(exception, "Debug action debug-list failed.");
            Line(report, "run", false, exception.Message);
        }
        finally
        {
            try
            {
                File.WriteAllText(path + ".partial", report.ToString());
                File.Move(path + ".partial", path, overwrite: true);
                Serilog.Log.Information("Debug action: List self-check written to {Path}.", path);
            }
            catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-list could not write {Path}.", path); }
            _running = false;
        }
    }

    private static void Line(StringBuilder report, string name, bool pass, string detail)
        => report.Append(pass ? "PASS " : "FAIL ").Append(name).Append(": ").AppendLine(detail);

    private static Task Wait(uint milliseconds)
    {
        var done = new TaskCompletionSource();
        MainLoop.Delay(milliseconds, done.SetResult);
        return done.Task;
    }

    private sealed record DebugRow(int Id, string Title, int Rank);

    private sealed class DebugCell : IWinoCell<DebugRow>
    {
        public static int ScopesDisposed;
        public static int Unbinds;
        private readonly Gtk.Label _label = Gtk.Label.New(string.Empty);

        public DebugCell()
        {
            _label.SetXalign(0);
            _label.SetMarginStart(12);
            _label.SetMarginTop(6);
            _label.SetMarginBottom(6);
        }

        public Gtk.Widget Root => _label;

        public void Bind(DebugRow item, WinoCellBinding binding)
        {
            _label.SetText($"Row {item.Id} · {item.Title}");
            binding.Scope.Own(new ActionDisposable(static () => ScopesDisposed++));
        }

        public void Unbind() => Unbinds++;
    }

    private sealed class InlineDispatcher : IDispatcher
    {
        public Task ExecuteOnUIThread(Action action)
        {
            action();
            return Task.CompletedTask;
        }
    }

    private sealed class Scene : IDisposable
    {
        private const int Rows = 50_000;
        private const int Ranks = 5;
        private readonly StringBuilder _report;
        private readonly ObservableCollection<DebugRow> _source;
        private readonly ObservableListModel<DebugRow> _model;
        private readonly ListSections<DebugRow> _sections;
        private readonly Gtk.MultiSelection _selection;
        private readonly WinoCellFactory<DebugCell, DebugRow> _factory;
        private readonly WinoHeaderFactory<DebugRow> _headers;
        private readonly Gtk.ListView _list;
        private readonly Gtk.ScrolledWindow _scroller;
        private readonly LoadMoreTrigger _loadMore;
        private readonly IDisposable _keeper;
        private readonly Adw.Window _window;
        private readonly long _attachMilliseconds;
        private readonly long _sortMilliseconds;
        private readonly List<ListChange> _changes = [];
        private int _nextId = Rows;
        private bool _loadArmed;
        private int _loads;
        private string _trace = string.Empty;

        public Scene(Adw.Application application, StringBuilder report, List<Exception> errors, List<WeakReference<DebugCell>> cells)
        {
            _report = report;
            DebugCell.ScopesDisposed = 0;
            DebugCell.Unbinds = 0;
            Action<Exception> error = exception =>
            {
                errors.Add(exception);
                Serilog.Log.Error(exception, "List self-check error.");
            };

            var random = new Random(7);
            var ids = Enumerable.Range(0, Rows).OrderBy(_ => random.Next()).ToArray();
            _source = new ObservableCollection<DebugRow>(ids.Select((id, index) => new DebugRow(id, $"Item {id}", index * Ranks / Rows)));

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            _model = new ObservableListModel<DebugRow>(new InlineDispatcher(), error);
            _model.Attach(_source);
            _attachMilliseconds = stopwatch.ElapsedMilliseconds;
            stopwatch.Restart();
            _sections = new ListSections<DebugRow>(_model.Store, static (a, b) => a.Rank.CompareTo(b.Rank), null, error);
            _sortMilliseconds = stopwatch.ElapsedMilliseconds;
            _model.Changed += _changes.Add;

            _selection = Gtk.MultiSelection.New(_sections.Model);
            _factory = new WinoCellFactory<DebugCell, DebugRow>(() =>
            {
                var cell = new DebugCell();
                cells.Add(new WeakReference<DebugCell>(cell));
                return cell;
            }, error);
            _headers = new WinoHeaderFactory<DebugRow>(static () =>
            {
                var label = Gtk.Label.New(string.Empty);
                label.SetXalign(0);
                label.AddCssClass("heading");
                label.SetMarginStart(12);
                label.SetMarginTop(12);
                return label;
            }, static (widget, row, count) =>
            {
                if (widget is Gtk.Label label) label.SetText($"Rank {row.Rank} ({count})");
            }, error);

            _list = Gtk.ListView.New(_selection, _factory.Factory);
            _list.SetHeaderFactory(_headers.Factory);
            WinoA11y.Label(_list, "List test rows");
            _scroller = Gtk.ScrolledWindow.New();
            _scroller.SetChild(_list);
            _scroller.SetVexpand(true);
            _loadMore = ListViews.LoadMoreNearEnd(_scroller, 600, () =>
            {
                if (!_loadArmed) return;
                _loadArmed = false;
                _loads++;
                for (var index = 0; index < 200; index++) _source.Add(NewRow(Ranks - 1));
            });
            _keeper = ListViews.KeepSelection(_model, _selection, _sections.PositionOf);

            var toolbar = Adw.ToolbarView.New();
            toolbar.AddTopBar(Adw.HeaderBar.New());
            toolbar.SetContent(_scroller);
            _window = Adw.Window.New();
            _window.SetApplication(application);
            _window.SetTitle("List test");
            _window.SetDefaultSize(1000, 700);
            _window.SetContent(toolbar);
            _window.Present();
        }

        public async Task RunAsync()
        {
            await Wait(600);
            Line(_report, "first splice", true, $"attach {Rows} rows {_attachMilliseconds} ms, first sort {_sortMilliseconds} ms");
            Line(_report, "sections", CheckSections(out var sectionDetail), sectionDetail);
            Line(_report, "source order kept", CheckOrder(out var orderDetail), orderDetail);
            Line(_report, "virtualised", _factory.CellCount is > 0 and < 300 && _factory.BoundCount <= _factory.CellCount, $"cells={_factory.CellCount} bound={_factory.BoundCount}");

            await AppendKeepsScroll();
            await MiddleChanges();

            Serilog.Log.Information("Debug action: List self-check holding for screenshots.");
            await Wait(10_000);

            await Coalesced();
            await SelectionKept();
            await ScrollAnchor();
            await LoadMore();
            Line(_report, "scopes", DebugCell.ScopesDisposed == DebugCell.Unbinds && _factory.BindMisses == 0,
                $"scopes disposed={DebugCell.ScopesDisposed} unbinds={DebugCell.Unbinds} bind misses={_factory.BindMisses}");
        }

        public void CollectItems(List<WeakReference<GObject.Object>> items, int count)
        {
            var available = Math.Min((uint)count, _model.Store.GetNItems());
            for (uint position = 0; position < available; position++)
            {
                if (_model.Store.GetObject(position) is { } item) items.Add(new WeakReference<GObject.Object>(item));
            }
        }

        public void Dispose()
        {
            _keeper.Dispose();
            _loadMore.Dispose();
            _list.SetModel(null);
            _selection.SetModel(null);
            _list.SetFactory(null);
            _list.SetHeaderFactory(null);
            _factory.Dispose();
            _headers.Dispose();
            _sections.Dispose();
            _model.Dispose();
            _source.Clear();
            _changes.Clear();
            _window.Close();
        }

        private async Task AppendKeepsScroll()
        {
            ListViews.ScrollTo(_list, FirstOfRank(2));
            await Wait(600);
            var before = _scroller.GetVadjustment().GetValue();
            _changes.Clear();
            var jumps = 0;
            var maxMove = 0.0;
            for (var index = 0; index < 20; index++)
            {
                _source.Add(NewRow(Ranks - 1));
                await Wait(100);
                var move = Math.Abs(_scroller.GetVadjustment().GetValue() - before);
                maxMove = Math.Max(maxMove, move);
                if (move > 0.5) jumps++;
            }
            var appends = _changes.Count(static change => change.IsAppend);
            Line(_report, "append keeps scroll", jumps == 0 && appends == 20 && _changes.Count == 20,
                $"value={before:0.0} largest move={maxMove:0.0} append splices={appends}/{_changes.Count}");
        }

        private async Task MiddleChanges()
        {
            var bad = new List<string>();
            for (var index = 0; index < 20; index++)
            {
                _changes.Clear();
                _source.Insert(_source.Count / 2, NewRow(index % Ranks));
                await Wait(100);
                if (_changes.Count != 1 || _changes[0].Removed + _changes[0].Added != 1) bad.Add($"insert {index}: {Describe()}");
            }

            _changes.Clear();
            _source.RemoveAt(_source.Count / 2);
            await Wait(100);
            if (_changes.Count != 1 || _changes[0].Removed + _changes[0].Added != 1) bad.Add($"remove: {Describe()}");

            _changes.Clear();
            _source.Move(_source.Count / 2, _source.Count / 2 + 10);
            await Wait(100);
            var move = Describe();
            _changes.Clear();
            _source[_source.Count / 3] = NewRow(1);
            await Wait(100);
            var replace = Describe();

            var ordered = CheckOrder(out var orderDetail);
            Line(_report, "middle changes", bad.Count == 0 && _model.Count == _source.Count && ordered,
                $"count={_model.Count}/{_source.Count} move {move} replace {replace} {orderDetail}{(bad.Count == 0 ? string.Empty : " bad: " + string.Join("; ", bad))}");
        }

        private async Task Coalesced()
        {
            _changes.Clear();
            for (var index = 0; index < 100; index++) _source.Add(NewRow(Ranks - 1));
            await Wait(300);
            Line(_report, "coalesced", _changes.Count == 1 && _changes[0].Added == 100 && _changes[0].IsAppend, $"100 adds -> {Describe()}");
        }

        private async Task SelectionKept()
        {
            ListViews.Select(_selection, [5, 15_000, 30_000]);
            var before = ListViews.SelectedValues<DebugRow>(_selection);
            _changes.Clear();
            _source.Move(0, _source.Count - 1);
            await Wait(300);
            var after = ListViews.SelectedValues<DebugRow>(_selection);
            var same = before.Length == 3 && after.Length == 3 && before.All(row => after.Contains(row));
            Line(_report, "selection kept", same && _changes.Count == 1 && _changes[0].IsReset,
                $"before=[{string.Join(", ", before.Select(static row => row.Id))}] after=[{string.Join(", ", after.Select(static row => row.Id))}] splice {Describe()}");
        }

        private async Task ScrollAnchor()
        {
            var valueBefore = _scroller.GetVadjustment().GetValue();
            var target = FirstOfRank(1) + 50;
            ListViews.ScrollTo(_list, target);
            await Wait(600);
            _trace = $"value {valueBefore:0} -> scroll to {target} -> {_scroller.GetVadjustment().GetValue():0}";
            if (ListViews.CaptureTop(_factory, _scroller) is not { } anchor)
            {
                Line(_report, "scroll anchor", false, "no bound row at the top");
                return;
            }
            _changes.Clear();
            _source.Move(_source.Count - 1, 0);
            await Wait(100);
            if (_sections.PositionOf(anchor.Item) is not { } position)
            {
                Line(_report, "scroll anchor", false, "the anchor row is gone");
                return;
            }
            ListViews.RestoreTop(_list, _factory, _scroller, anchor, position);
            await Wait(300);
            _trace += $"; after restore {_scroller.GetVadjustment().GetValue():0}";
            var found = _factory.TryGetTop(_scroller, out var top, out var offset);
            Line(_report, "scroll anchor", found && ReferenceEquals(top, anchor.Item) && Math.Abs(offset - anchor.Offset) <= 2 && _changes.Count == 1 && _changes[0].IsReset,
                $"{_trace}; anchor row {anchor.Item.Id} at {anchor.Offset:0.0} position {position}, after reset row {top?.Id} at {offset:0.0}, splice {Describe()}");
        }

        private async Task LoadMore()
        {
            var countBefore = _model.Count;
            ListViews.ScrollTo(_list, 0);
            await Wait(300);
            _loadArmed = true;
            ListViews.ScrollTo(_list, _sections.Model.GetNItems() - 1);
            await Wait(500);
            _loadArmed = false;
            var adjustment = _scroller.GetVadjustment();
            Line(_report, "load more", _loads >= 1 && _model.Count == countBefore + 200 && adjustment.GetValue() > 0,
                $"loads={_loads} count {countBefore} -> {_model.Count} value={adjustment.GetValue():0.0} page={adjustment.GetPageSize():0.0} upper={adjustment.GetUpper():0.0}");
        }

        private bool CheckSections(out string detail)
        {
            var count = _sections.Model.GetNItems();
            var ranks = new List<string>();
            var previous = -1;
            var ok = true;
            uint position = 0;
            while (position < count)
            {
                var (start, end) = _sections.SectionAt(position);
                if (end <= start || _sections.ItemAt(start) is not { } first)
                {
                    detail = $"empty section at {position}";
                    return false;
                }
                for (var index = start; index < end; index++)
                {
                    if (_sections.ItemAt(index)?.Rank != first.Rank) ok = false;
                }
                if (first.Rank <= previous) ok = false;
                previous = first.Rank;
                ranks.Add($"{first.Rank}:{end - start}");
                position = end;
            }
            detail = $"{ranks.Count} sections [{string.Join(", ", ranks)}]";
            return ok && ranks.Count == Ranks;
        }

        private bool CheckOrder(out string detail)
        {
            var expected = _source.OrderBy(static row => row.Rank).ToArray();
            var count = (int)_sections.Model.GetNItems();
            if (count != expected.Length)
            {
                detail = $"order: {count} rows on screen, {expected.Length} in the source";
                return false;
            }
            var mismatches = 0;
            for (var index = 0; index < count; index++)
            {
                if (index >= 2000 && index < count - 2000) continue;
                if (!ReferenceEquals(_sections.ItemAt((uint)index), expected[index])) mismatches++;
            }
            detail = $"order mismatches={mismatches} of {Math.Min(count, 4000)}";
            return mismatches == 0;
        }

        private uint FirstOfRank(int rank)
        {
            uint position = 0;
            var count = _sections.Model.GetNItems();
            while (position < count)
            {
                var (start, end) = _sections.SectionAt(position);
                if (_sections.ItemAt(start)?.Rank == rank) return start;
                position = end;
            }
            return 0;
        }

        private DebugRow NewRow(int rank)
        {
            var id = _nextId++;
            return new DebugRow(id, $"Added {id}", rank);
        }

        private string Describe()
            => _changes.Count == 0 ? "no splice" : string.Join(" ", _changes.Select(static change => $"(at {change.Position} -{change.Removed} +{change.Added}{(change.IsAppend ? " append" : string.Empty)}{(change.IsReset ? " reset" : string.Empty)})"));
    }
}
#endif
