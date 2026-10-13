using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.UIKit.Views.Mail;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Phone.Mail;

/// <summary>
/// The bottom toolbar the phone shell attaches while the shared reader page is on top of the Mail stack: Archive,
/// Delete, Move, Flag, a Reply menu (Reply, Reply all, Forward) and More. It is built from the reader's own command
/// model (<see cref="IMailReaderCommands"/>, the same commands the iPad reader shows in its command bar): availability,
/// titles, enabled state and the Archive/Unarchive and Flag/Clear flag variants come from
/// <see cref="IMailReaderCommands.PrimaryCommands"/>, More is <see cref="IMailReaderCommands.CreateMoreMenu"/>, and the
/// toolbar rebuilds on <see cref="IMailReaderCommands.CommandsChanged"/>. Move runs the reader's Move command with its
/// button as the anchor, which opens the phone Move sheet through <c>IMailMovePresenter</c>.
/// </summary>
public sealed class MailReaderToolbar : IDisposable
{
    private static readonly MailOperation[] ArchiveOperations = [MailOperation.UnArchive, MailOperation.Archive];
    private static readonly MailOperation[] DeleteOperations = [MailOperation.SoftDelete, MailOperation.HardDelete];
    private static readonly MailOperation[] FlagOperations = [MailOperation.ClearFlag, MailOperation.SetFlag];
    private static readonly MailOperation[] ReplyOperations = [MailOperation.Reply, MailOperation.ReplyAll, MailOperation.Forward];

    private readonly IMailReaderCommands _commands;
    private readonly UIButton _archive;
    private readonly UIButton _delete;
    private readonly UIButton _moveItem;
    private readonly UIButton _flag;
    private readonly UIButton _reply;
    private readonly UIButton _more;
    private MailReaderCommand? _archiveCommand, _deleteCommand, _moveCommand, _flagCommand;
    private bool _disposed;

    public MailReaderToolbar(IMailReaderCommands commands)
    {
        _commands = commands;
        // The phone shows these commands in this toolbar, not in the page's own bar.
        _commands.ShowsCommandBar = false;
        _archive = PhoneBarItems.RowButton(WinoIconGlyph.Archive, Translator.MailOperation_Archive, () => _archiveCommand?.Invoke(_archive));
        _delete = PhoneBarItems.RowButton(WinoIconGlyph.Delete, Translator.MailOperation_Delete, () => _deleteCommand?.Invoke(_delete));
        _moveItem = PhoneBarItems.RowButton(WinoIconGlyph.Move, Translator.MailOperation_Move, () => _moveCommand?.Invoke(_moveItem));
        _flag = PhoneBarItems.RowButton(WinoIconGlyph.Flag, Translator.MailOperation_Flag, () => _flagCommand?.Invoke(_flag));
        _reply = PhoneBarItems.RowButton(WinoIconGlyph.Reply, Translator.MailOperation_Reply, null);
        _more = PhoneBarItems.RowButton(WinoIconGlyph.More, Translator.More, null);
        // More is built when it opens, so it always shows the reader's current overflow (read state, junk, print...).
        _more.Menu = UIMenu.Create([UIDeferredMenuElement.CreateUncached(completion => completion(_commands.CreateMoreMenu(_more).Children))]);
        Buttons = [_archive, _delete, _moveItem, _flag, _reply, _more];
        Items = PhoneBarItems.Row(Buttons);
        _commands.CommandsChanged += CommandsChanged;
        Update();
    }

    /// <summary>The toolbar items for the reader controller's <c>ToolbarItems</c>.</summary>
    public UIBarButtonItem[] Items { get; }

    /// <summary>The command buttons, in toolbar order (accessibility and the debug bridge).</summary>
    public UIButton[] Buttons { get; }

    private void CommandsChanged(object? sender, EventArgs args) => Update();

    private MailReaderCommand? Find(params MailOperation[] operations)
    {
        foreach (var operation in operations)
            if (_commands.PrimaryCommands.FirstOrDefault(command => command?.Operation == operation) is { } command) return command;
        return null;
    }

    private static void Apply(UIButton button, MailReaderCommand? command, WinoIconGlyph fallback)
    {
        button.Enabled = command?.IsEnabled == true;
        var glyph = command?.Glyph ?? fallback;
        if (button.Configuration is { } configuration)
        {
            configuration.Image = WinoIcons.TemplateImage(glyph, PhoneBarItems.RowGlyphSize);
            button.Configuration = configuration;
        }
        if (command is not null) button.AccessibilityLabel = command.Title;
    }

    private void Update()
    {
        if (_disposed) return;
        _archiveCommand = Find(ArchiveOperations);
        _deleteCommand = Find(DeleteOperations);
        _moveCommand = Find(MailOperation.Move);
        _flagCommand = Find(FlagOperations);
        Apply(_archive, _archiveCommand, WinoIconGlyph.Archive);
        Apply(_delete, _deleteCommand, WinoIconGlyph.Delete);
        Apply(_moveItem, _moveCommand, WinoIconGlyph.Move);
        Apply(_flag, _flagCommand, WinoIconGlyph.Flag);

        var replies = ReplyOperations.Select(operation => Find(operation)).OfType<MailReaderCommand>().ToArray();
        _reply.Menu = UIMenu.Create(replies.Select(command => (UIMenuElement)command.CreateAction(_reply)).ToArray());
        _reply.Enabled = replies.Any(static command => command.IsEnabled);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _commands.CommandsChanged -= CommandsChanged;
    }
}
