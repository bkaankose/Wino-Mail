using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Editor;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Mail.Compose;

/// <summary>
/// The Insert commands of an <see cref="EditorFormatToolbar"/> (Mac order): Picture, Emoji, Table (with the Windows row and column
/// commands), Link, Remove link and Image properties, with Ctrl+K from the editor opening the link form. Every command ends with the editor focused.
/// </summary>
internal sealed class EditorInsertItems : IDisposable
{
    private readonly EditorFormatToolbar _toolbar;
    private readonly Func<Gtk.Window?> _window;
    private readonly Action<Exception> _error;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly EditorFormatButton? _removeLink;
    private readonly EditorFormatButton? _imageProperties;
    private readonly Gtk.MenuButton? _emoji;
    private readonly List<EditorFormatButton> _buttons = [];
    private readonly List<Gtk.Button> _tableButtons = [];
    private bool _disposed;

    public EditorInsertItems(EditorFormatToolbar toolbar, EditorToolbarFeatures features, Func<Gtk.Window?> window, Action<Exception> error)
    {
        WinoLiveObjects.Track(this);
        _toolbar = toolbar;
        _window = window;
        _error = error;
        var items = new List<EditorToolbarItem>();

        if (features.Image)
            items.Add(Button(WinoIconGlyph.ImageAdd, Translator.Composer_InsertPicture, () => Observe(InsertPicturesAsync()), Translator.Composer_InsertPictureDescription));
        if (features.Emoji)
        {
            var emoji = _emoji = Gtk.MenuButton.New();
            emoji.SetChild(WinoIcons.Label(WinoIconGlyph.Emoji));
            emoji.SetHasFrame(false);
            emoji.SetValign(Gtk.Align.Center);
            emoji.AddCssClass("wino-editor-tool");
            WinoA11y.LabelWithTooltip(emoji, Translator.Emoji);
            emoji.SetTooltipText(Translator.Composer_InsertEmojiDescription);
            var chooser = Gtk.EmojiChooser.New();
            _subscriptions.Add(GtkSignals.Connect<Gtk.EmojiChooser, Gtk.EmojiChooser.EmojiPickedSignalArgs>(
                h => chooser.OnEmojiPicked += h, h => chooser.OnEmojiPicked -= h, (_, args) => InsertEmoji(args.Text)));
            emoji.SetPopover(chooser);
            items.Add(new EditorToolbarItem(emoji, menu => menu.Item(Translator.Emoji, () => emoji.Popup(), icon: WinoIconGlyph.Emoji)));
        }
        if (features.Table)
        {
            items.Add(Button(WinoIconGlyph.Table, Translator.Composer_Table, () => Observe(InsertTableAsync()), Translator.Composer_InsertTable));
            items.Add(TableButton(Translator.Composer_TableAddRow, "addRow"));
            items.Add(TableButton(Translator.Composer_TableRemoveRow, "removeRow"));
            items.Add(TableButton(Translator.Composer_TableAddColumn, "addColumn"));
            items.Add(TableButton(Translator.Composer_TableRemoveColumn, "removeColumn"));
            items.Add(TableButton(Translator.Composer_TableDelete, "deleteTable"));
        }
        items.Add(Button(WinoIconGlyph.Link, Translator.Composer_Link, () => Observe(OpenLinkDialogAsync()), Translator.Composer_InsertLink, "<Control>k"));
        if (features.LinkTools)
        {
            var removeLink = _removeLink = Track(new EditorFormatButton(WinoIconGlyph.LinkDismiss, Translator.Composer_RemoveLink, RemoveLink) { Enabled = false });
            items.Add(new EditorToolbarItem(removeLink.Root, menu => menu.Item(Translator.Composer_RemoveLink, RemoveLink, CanRemoveLink(toolbar.CurrentState), icon: WinoIconGlyph.LinkDismiss)));
            var properties = _imageProperties = Track(new EditorFormatButton(WinoIconGlyph.Edit, Translator.Composer_ImageProperties, () => Observe(EditImagePropertiesAsync()),
                tooltip: Translator.Composer_ImagePropertiesDescription) { Enabled = false });
            items.Add(new EditorToolbarItem(properties.Root, menu => menu.Item(Translator.Composer_ImageProperties, () => Observe(EditImagePropertiesAsync()), toolbar.CurrentState.IsImageSelected, icon: WinoIconGlyph.Edit)));
        }

        toolbar.AddInsertItems([.. items]);
        toolbar.ShortcutRequested += ShortcutRequested;
        toolbar.ImageInsertionRequested += ImageInsertionRequested;
        toolbar.StateApplied += StateApplied;
        StateApplied(toolbar.CurrentState);
    }

    public async Task OpenLinkDialogAsync()
    {
        if (_disposed || _toolbar.Session is not { } session || _window() is not { } window) return;
        var args = await EditorDialogs.LinkAsync(window, session.CurrentState);
        if (args is not null && !_disposed) await _toolbar.RunAsync(session, EditorCommand.InsertLink(args));
    }

    public async Task EditImagePropertiesAsync()
    {
        if (_disposed || _toolbar.Session is not { } session || _window() is not { } window) return;
        var args = await EditorDialogs.ImagePropertiesAsync(window, session.CurrentState);
        if (args is not null && !_disposed) await _toolbar.RunAsync(session, EditorCommand.SetImageProperties(args));
    }

    public async Task InsertTableAsync()
    {
        if (_disposed || _toolbar.Session is not { } session || _window() is not { } window) return;
        var args = await EditorDialogs.TableAsync(window);
        if (args is not null && !_disposed) await _toolbar.RunAsync(session, EditorCommand.InsertTable(args));
    }

    public async Task InsertPicturesAsync()
    {
        if (_disposed || _toolbar.Session is not { } session || _window() is not { } window) return;
        var images = await EditorDialogs.PickImagesAsync(window);
        await InsertImagesAsync(session, images);
    }

    public async Task InsertImagesAsync(Wino.Editor.WebKitGtk.WebKitGtkHtmlMailEditorSession session, EditorImageInfo[] images)
    {
        if (images.Length == 0 || _disposed || !ReferenceEquals(_toolbar.Session, session)) return;
        await session.InsertImagesAsync(images);
        await session.FocusEditorAsync(true);
    }

    public void InsertEmoji(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        _emoji?.Popdown();
        _toolbar.Run(new EditorCommand(EditorCommandKind.InsertEmoji, text));
    }

    public async Task RunTableCommandAsync(string command)
    {
        if (_disposed || _toolbar.Session is not { } session || !session.IsInTable) return;
        await session.TableCommandAsync(command);
        if (!_disposed) await session.FocusEditorAsync(true);
    }

    private bool IsInTable => _toolbar.Session is { IsInTable: true };

    private void RemoveLink() => _toolbar.Run(EditorCommand.RemoveLink());

    private static bool CanRemoveLink(EditorState state)
        => !string.IsNullOrWhiteSpace(state.LinkUrl) || (state.IsImageSelected && !string.IsNullOrWhiteSpace(state.ImageLinkUrl));

    private void StateApplied(EditorState state)
    {
        if (_removeLink is not null) _removeLink.Enabled = CanRemoveLink(state);
        if (_imageProperties is not null) _imageProperties.Enabled = state.IsImageSelected;
        var inTable = IsInTable;
        foreach (var button in _tableButtons) button.SetSensitive(inTable);
    }

    private void ShortcutRequested(EditorShortcutKind kind)
    {
        if (kind == EditorShortcutKind.OpenLinkDialog) Observe(OpenLinkDialogAsync());
    }

    private void ImageInsertionRequested() => Observe(InsertPicturesAsync());

    private EditorToolbarItem Button(WinoIconGlyph glyph, string label, Action action, string tooltip, string? accelerator = null)
    {
        var keys = accelerator is not null && Infrastructure.ShortcutKeys.TryParseAccelerator(accelerator, out var key, out var modifiers)
            ? $" ({Gtk.Functions.AcceleratorGetLabel(key, modifiers)})"
            : string.Empty;
        var button = Track(new EditorFormatButton(glyph, label, action, tooltip: tooltip + keys));
        return new EditorToolbarItem(button.Root, menu => menu.Item(label, action, true, accelerator, glyph));
    }

    // Windows shows these as text commands (no glyph); they act on the table that holds the caret.
    private EditorToolbarItem TableButton(string label, string command)
    {
        var button = Gtk.Button.NewWithLabel(label);
        button.SetHasFrame(false);
        button.SetValign(Gtk.Align.Center);
        button.AddCssClass("wino-editor-tool");
        button.SetTooltipText(label);
        button.SetSensitive(false);
        WinoA11y.Rename(button, label);
        _subscriptions.Add(GtkSignals.OnClicked(button, () => Observe(RunTableCommandAsync(command))));
        _tableButtons.Add(button);
        return new EditorToolbarItem(button, menu => menu.Item(label, () => Observe(RunTableCommandAsync(command)), IsInTable));
    }

    private EditorFormatButton Track(EditorFormatButton button)
    {
        _buttons.Add(button);
        return button;
    }

    private async void Observe(Task task)
    {
        try { await task; }
        catch (ObjectDisposedException) { }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (!_disposed) _error(exception); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _toolbar.ShortcutRequested -= ShortcutRequested;
        _toolbar.ImageInsertionRequested -= ImageInsertionRequested;
        _toolbar.StateApplied -= StateApplied;
        _emoji?.Popdown();
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        foreach (var button in _buttons) button.Dispose();
        _buttons.Clear();
        _tableButtons.Clear();
    }
}
