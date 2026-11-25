using System.Collections.Specialized;
using AppKit;
using CoreGraphics;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.AppKit.ToDo;
using Wino.Mail.ViewModels.FlaggedMail;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.ToDo;

/// <summary>
/// Flagged mail in To Do (Windows ToDoPage flagged mail list): the flagged messages of every account. The check completes
/// one by clearing its flag; a click opens the message.
/// </summary>
public sealed partial class ToDoPageViewController
{
    private const double FlaggedRowHeight = 56;

    private NSView _flaggedSurface = null!;
    private NSTableView _flaggedTable = null!;
    private NSView _flaggedEmpty = null!;
    private bool _flaggedReloadScheduled;

    private NSView BuildFlaggedMailSurface()
    {
        var surface = new NSView { TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };

        _flaggedTable = new FlaggedMailTableView(this)
        {
            HeaderView = null,
            Style = NSTableViewStyle.Plain,
            IntercellSpacing = new CGSize(0, 2),
            AllowsMultipleSelection = false,
            AllowsEmptySelection = true,
            AllowsTypeSelect = false,
            BackgroundColor = NSColor.Clear,
            SelectionHighlightStyle = NSTableViewSelectionHighlightStyle.None,
            ColumnAutoresizingStyle = NSTableViewColumnAutoresizingStyle.FirstColumnOnly
        };
        _flaggedTable.AddColumn(new NSTableColumn("flagged") { ResizingMask = NSTableColumnResizing.Autoresizing, Width = 400 });
        _flaggedTable.DataSource = new FlaggedMailDataSource(this);
        _flaggedTable.Delegate = new FlaggedMailDelegate(this);
        WinoAccessibility.Label(_flaggedTable, Translator.ToDoPage_FlaggedMail);

        var scroll = new NSScrollView
        {
            DocumentView = _flaggedTable,
            HasVerticalScroller = true,
            AutohidesScrollers = true,
            DrawsBackground = false,
            BorderType = NSBorderType.NoBorder,
            TranslatesAutoresizingMaskIntoConstraints = false,
            ContentInsets = new NSEdgeInsets(0, 16, 4, 16),
            AutomaticallyAdjustsContentInsets = false
        };

        var emptyTitle = WinoStyle.Label(Translator.ToDoPage_FlaggedMailEmptyTitle, WinoStyle.BodyStrong, maximumLines: 2);
        emptyTitle.Alignment = NSTextAlignment.Center;
        var emptyBody = WinoStyle.Label(Translator.ToDoPage_FlaggedMailEmptyBody, WinoStyle.Body, WinoStyle.SecondaryText, 0);
        emptyBody.Alignment = NSTextAlignment.Center;
        var emptyStack = WinoLayout.VStack(6, new WinoIconView(WinoIconGlyph.Flag, 48, WinoStyle.TertiaryText), emptyTitle, emptyBody);
        emptyStack.Alignment = NSLayoutAttribute.CenterX;
        emptyStack.WidthAnchor.ConstraintLessThanOrEqualTo(320).Active = true;
        _flaggedEmpty = emptyStack;
        _flaggedEmpty.Hidden = true;

        surface.AddSubview(scroll);
        surface.AddSubview(_flaggedEmpty);
        NSLayoutConstraint.ActivateConstraints(
        [
            scroll.TopAnchor.ConstraintEqualTo(surface.TopAnchor),
            scroll.LeadingAnchor.ConstraintEqualTo(surface.LeadingAnchor),
            scroll.TrailingAnchor.ConstraintEqualTo(surface.TrailingAnchor),
            scroll.BottomAnchor.ConstraintEqualTo(surface.BottomAnchor),
            _flaggedEmpty.CenterXAnchor.ConstraintEqualTo(surface.CenterXAnchor),
            _flaggedEmpty.CenterYAnchor.ConstraintEqualTo(surface.CenterYAnchor)
        ]);
        return surface;
    }

    private void BindFlaggedMail()
    {
        ViewModel.FlaggedMail.Items.CollectionChanged += FlaggedItemsChanged;
        Bind(nameof(ViewModel.IsFlaggedMailSelected), static vm => vm.IsFlaggedMailSelected, selected =>
        {
            _flaggedSurface.Hidden = !selected;
            _scroll.Hidden = selected;
            ScheduleFlaggedReload();
        });
    }

    private void ReleaseFlaggedMail() => ViewModel.FlaggedMail.Items.CollectionChanged -= FlaggedItemsChanged;

    private void FlaggedItemsChanged(object? sender, NotifyCollectionChangedEventArgs args) => ScheduleFlaggedReload();

    private void ScheduleFlaggedReload()
    {
        if (_released || _flaggedReloadScheduled) return;
        _flaggedReloadScheduled = true;
        NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            _flaggedReloadScheduled = false;
            if (_released) return;
            _flaggedTable.ReloadData();
            _flaggedEmpty.Hidden = !ViewModel.IsFlaggedMailSelected || ViewModel.FlaggedMail.Items.Count > 0;
        });
    }

    private FlaggedMailItemViewModel? FlaggedItemAt(nint row)
        => row >= 0 && row < ViewModel.FlaggedMail.Items.Count ? ViewModel.FlaggedMail.Items[(int)row] : null;

    private void OpenFlaggedMail(nint row)
    {
        if (FlaggedItemAt(row) is { } item) Observe(ViewModel.FlaggedMail.OpenCommand.ExecuteAsync(item));
    }

    private void CompleteFlaggedMail(FlaggedMailItemViewModel item) => Observe(ViewModel.FlaggedMail.CompleteCommand.ExecuteAsync(item));

    private sealed class FlaggedMailDataSource(ToDoPageViewController owner) : NSTableViewDataSource
    {
        public override nint GetRowCount(NSTableView tableView) => owner.ViewModel.FlaggedMail.Items.Count;
    }

    private sealed class FlaggedMailDelegate(ToDoPageViewController owner) : NSTableViewDelegate
    {
        public override NSView GetViewForItem(NSTableView tableView, NSTableColumn? tableColumn, nint row)
            => owner.FlaggedItemAt(row) is { } item ? new FlaggedMailRow(owner, item) : new NSView();

        public override nfloat GetRowHeight(NSTableView tableView, nint row) => (nfloat)FlaggedRowHeight;

        public override bool ShouldSelectRow(NSTableView tableView, nint row) => false;
    }

    private sealed class FlaggedMailTableView(ToDoPageViewController owner) : NSTableView
    {
        public override void MouseUp(NSEvent theEvent)
        {
            base.MouseUp(theEvent);
            if (theEvent.ClickCount != 1) return;
            owner.OpenFlaggedMail(GetRow(ConvertPointFromView(theEvent.LocationInWindow, null)));
        }
    }

    private sealed class FlaggedMailRow : NSView
    {
        private readonly ToDoPageViewController _owner;
        private readonly FlaggedMailItemViewModel _item;
        private readonly NSButton _complete;

        public FlaggedMailRow(ToDoPageViewController owner, FlaggedMailItemViewModel item)
        {
            _owner = owner;
            _item = item;
            TranslatesAutoresizingMaskIntoConstraints = false;

            _complete = WinoToDoStyle.IconButton(WinoIconGlyph.CheckmarkCircle, Translator.ToDoPage_FlaggedMailComplete, 18, 28, 28, WinoStyle.SecondaryText);
            _complete.ToolTip = Translator.ToDoPage_FlaggedMailComplete;
            _complete.Enabled = !item.IsCompleting;
            _complete.OnActivated(this, static row => row.Complete());

            var title = WinoStyle.Label(item.Subject, WinoStyle.BodyStrong, WinoStyle.PrimaryText, 1);
            var details = WinoStyle.Label(item.DetailsText, WinoStyle.Caption, WinoStyle.SecondaryText, 1);
            var text = WinoLayout.VStack(2, title, details);
            var flag = new WinoIconView(WinoIconGlyph.Flag, 14, WinoStyle.Critical);
            var row = WinoLayout.HStack(10, _complete, text, WinoLayout.Spacer(), flag);
            row.Alignment = NSLayoutAttribute.CenterY;
            WinoLayout.Fill(row, this, 6, 8, 6, 8);
            WinoAccessibility.Label(this, item.Subject);
        }

        private void Complete() => _owner.CompleteFlaggedMail(_item);
    }
}
