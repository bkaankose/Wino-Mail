using System.Collections.Specialized;
using System.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Mail.Compose;

/// <summary>
/// The composer's attachments (Mac attachment tray): a summary line with Remove all, then one chip per file with its system
/// icon, name, size and a remove button, and Open, Save and Remove on its context menu. Hidden while empty.
/// </summary>
internal sealed class ComposeAttachmentTray : IDisposable
{
    private readonly ComposePageViewModel _viewModel;
    private readonly IDispatcher _dispatcher;
    private readonly Action<Exception> _error;
    private readonly Gtk.Box _root = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
    private readonly Gtk.Label _summary = Gtk.Label.New(string.Empty);
    private readonly Gtk.Button _removeAll = Gtk.Button.NewWithLabel(Translator.Composer_RemoveAllAttachments);
    private readonly Adw.WrapBox _chips = Adw.WrapBox.New();
    private readonly List<IDisposable> _subscriptions = [];
    private readonly List<Chip> _chipList = [];
    private readonly MainLoop.Coalescer _rebuild;
    private bool _disposed;

    public ComposeAttachmentTray(ComposePageViewModel viewModel, IDispatcher dispatcher, Action<Exception> error)
    {
        WinoLiveObjects.Track(this);
        _viewModel = viewModel;
        _dispatcher = dispatcher;
        _error = error;
        _rebuild = new MainLoop.Coalescer(Rebuild);

        _root.AddCssClass("wino-compose-attachments");
        _root.SetMarginStart(12);
        _root.SetMarginEnd(12);
        _root.SetMarginBottom(12);
        var header = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        header.Append(WinoIcons.Label(WinoIconGlyph.Attachment, 14));
        _summary.AddCssClass("dim-label");
        _summary.AddCssClass("caption");
        _summary.SetXalign(0);
        _summary.SetHexpand(true);
        _summary.SetEllipsize(Pango.EllipsizeMode.End);
        header.Append(_summary);
        _removeAll.AddCssClass("flat");
        _removeAll.AddCssClass("link");
        header.Append(_removeAll);
        _root.Append(header);

        _chips.SetChildSpacing(8);
        _chips.SetLineSpacing(8);
        _chips.AccessibleRole = Gtk.AccessibleRole.List;
        WinoA11y.Label(_chips, Translator.Composer_AttachmentsListLabel);
        _root.Append(_chips);
        _root.SetVisible(false);

        _subscriptions.Add(GtkSignals.OnClicked(_removeAll, () =>
        {
            if (_viewModel.RemoveAllAttachmentsCommand.CanExecute(null)) _viewModel.RemoveAllAttachmentsCommand.Execute(null);
        }));
        NotifyCollectionChangedEventHandler changed = (_, _) => _ = _dispatcher.ExecuteOnUIThread(_rebuild.Request);
        _viewModel.IncludedAttachments.CollectionChanged += changed;
        _subscriptions.Add(new ActionDisposable(() => _viewModel.IncludedAttachments.CollectionChanged -= changed));
        Rebuild();
    }

    public Gtk.Widget Root => _root;
    public int ChipCount => _chipList.Count;

    private void Rebuild()
    {
        if (_disposed) return;
        foreach (var chip in _chipList)
        {
            _chips.Remove(chip.Root);
            chip.Dispose();
        }
        _chipList.Clear();
        foreach (var attachment in _viewModel.IncludedAttachments.ToArray())
        {
            var chip = new Chip(attachment, this);
            _chipList.Add(chip);
            _chips.Append(chip.Root);
        }
        _summary.SetText(_viewModel.AttachmentsSummary ?? string.Empty);
        _root.SetVisible(_chipList.Count > 0);
    }

    private void Open(MailAttachmentViewModel attachment) => Observe(_viewModel.OpenAttachmentCommand.ExecuteAsync(attachment));

    private void Save(MailAttachmentViewModel attachment) => Observe(_viewModel.SaveAttachmentCommand.ExecuteAsync(attachment));

    private void Remove(MailAttachmentViewModel attachment)
    {
        if (_viewModel.RemoveAttachmentCommand.CanExecute(attachment)) _viewModel.RemoveAttachmentCommand.Execute(attachment);
    }

    private async void Observe(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (!_disposed) _error(exception); }
    }

#if DEBUG
    internal string Describe() => string.Join('\n', _viewModel.IncludedAttachments.Select(static attachment =>
        $"{attachment.FileName} | {attachment.ReadableSize} | {attachment.Content?.Length ?? 0} | busy={attachment.IsBusy}"))
        + $"\nchips={_chipList.Count} visible={_root.GetVisible()} summary={_summary.GetText()}";
#endif

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _rebuild.Dispose();
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        foreach (var chip in _chipList) chip.Dispose();
        _chipList.Clear();
    }

    private sealed class Chip : IDisposable
    {
        private readonly MailAttachmentViewModel _attachment;
        private readonly Adw.Spinner _spinner = Adw.Spinner.New();
        private readonly List<IDisposable> _subscriptions = [];

        public Chip(MailAttachmentViewModel attachment, ComposeAttachmentTray tray)
        {
            WinoLiveObjects.Track(this);
            _attachment = attachment;
            Root = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
            Root.AddCssClass("wino-compose-attachment");
            Root.AccessibleRole = Gtk.AccessibleRole.ListItem;
            WinoA11y.Label(Root, $"{attachment.FileName}, {attachment.ReadableSize}");

            var open = Gtk.Button.New();
            open.AddCssClass("flat");
            var content = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
            content.Append(AttachmentFileIcons.Image(attachment.FileName, 24));
            var text = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
            var name = Gtk.Label.New(attachment.FileName);
            name.SetEllipsize(Pango.EllipsizeMode.Middle);
            name.SetMaxWidthChars(24);
            name.SetWidthChars(Math.Min(attachment.FileName.Length, 24));
            name.SetXalign(0);
            name.AddCssClass("wino-attachment-name");
            var size = Gtk.Label.New(attachment.ReadableSize);
            size.SetXalign(0);
            size.AddCssClass("dim-label");
            size.AddCssClass("caption");
            text.Append(name);
            text.Append(size);
            content.Append(text);
            _spinner.SetSizeRequest(14, 14);
            _spinner.SetVisible(attachment.IsBusy);
            content.Append(_spinner);
            open.SetChild(content);
            open.SetTooltipText(attachment.FileName);
            WinoA11y.Rename(open, $"{Translator.Buttons_Open} {attachment.FileName}");
            Root.Append(open);

            var remove = WinoIcons.Button(WinoIconGlyph.Dismiss, $"{Translator.Buttons_Remove} {attachment.FileName}", 12);
            remove.SetValign(Gtk.Align.Center);
            Root.Append(remove);

            _subscriptions.Add(GtkSignals.OnClicked(open, () => tray.Open(attachment)));
            _subscriptions.Add(GtkSignals.OnClicked(remove, () => tray.Remove(attachment)));
            _subscriptions.Add(ContextMenu.Attach(Root, menu => menu
                .Item(Translator.Buttons_Open, () => tray.Open(attachment), icon: WinoIconGlyph.OpenInNewWindow)
                .Item(Translator.Buttons_Save, () => tray.Save(attachment), icon: WinoIconGlyph.Save)
                .Separator()
                .Item(Translator.Buttons_Remove, () => tray.Remove(attachment), icon: WinoIconGlyph.Dismiss, destructive: true)));
            PropertyChangedEventHandler busy = (_, args) =>
            {
                if (args.PropertyName is nameof(MailAttachmentViewModel.IsBusy) or null)
                    _ = tray._dispatcher.ExecuteOnUIThread(() => _spinner.SetVisible(_attachment.IsBusy));
            };
            attachment.PropertyChanged += busy;
            _subscriptions.Add(new ActionDisposable(() => attachment.PropertyChanged -= busy));
        }

        public Gtk.Box Root { get; }

        public void Dispose()
        {
            foreach (var subscription in _subscriptions) subscription.Dispose();
            _subscriptions.Clear();
        }
    }
}
