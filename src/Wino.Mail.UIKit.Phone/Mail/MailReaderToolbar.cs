using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.UIKit.Views.Mail;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Phone.Mail;

/// <summary>
/// The bottom toolbar the phone shell attaches while the shared reader page is on top of the Mail stack. It is built
/// from the reader's own command model (<see cref="IMailReaderCommands"/>, the same commands the iPad reader shows in its
/// command bar), so it follows the Reader actions setting: the shown actions in their order, Reply, Reply all and
/// Forward folded into one Reply menu where the first of them sits, at most <see cref="SlotCount"/> buttons, then More
/// with the actions that did not fit ahead of <see cref="IMailReaderCommands.CreateMoreMenu"/>. The toolbar rebuilds on
/// <see cref="IMailReaderCommands.CommandsChanged"/>. Move runs the reader's Move command with its button as the anchor,
/// which opens the phone Move sheet through <c>IMailMovePresenter</c>.
/// </summary>
public sealed class MailReaderToolbar : IDisposable
{
    private const int SlotCount = 5;

    private readonly IMailReaderCommands _commands;
    private readonly UIButton[] _slots = new UIButton[SlotCount];
    private readonly MailReaderCommand?[] _slotCommands = new MailReaderCommand?[SlotCount];
    private readonly UIButton _more;
    private MailReaderCommand[] _spilled = [];
    private bool _disposed;

    public MailReaderToolbar(IMailReaderCommands commands)
    {
        _commands = commands;
        // The phone shows these commands in this toolbar, not in the page's own bar.
        _commands.ShowsCommandBar = false;
        for (int index = 0; index < SlotCount; index++)
        {
            int slot = index;
            _slots[slot] = PhoneBarItems.RowButton(WinoIconGlyph.More, string.Empty, () => _slotCommands[slot]?.Invoke(_slots[slot]));
        }
        _more = PhoneBarItems.RowButton(WinoIconGlyph.More, Translator.More, null);
        // More is built when it opens, so it always shows the reader's current overflow (read state, junk, print...).
        _more.Menu = UIMenu.Create([UIDeferredMenuElement.CreateUncached(completion => completion(MoreElements()))]);
        Items = PhoneBarItems.Row([.. _slots, _more]);
        _commands.CommandsChanged += CommandsChanged;
        Update();
    }

    /// <summary>The toolbar items for the reader controller's <c>ToolbarItems</c>.</summary>
    public UIBarButtonItem[] Items { get; }

    /// <summary>The shown command buttons, in toolbar order (accessibility and the debug bridge).</summary>
    public UIButton[] Buttons => [.. _slots.Where(static button => !button.Hidden), _more];

    private void CommandsChanged(object? sender, EventArgs args) => Update();

    private static bool IsReply(MailOperation operation) => operation is MailOperation.Reply or MailOperation.ReplyAll or MailOperation.Forward;

    private UIMenuElement[] MoreElements()
    {
        var spilled = _spilled.Select(command => (UIMenuElement)command.CreateAction(_more)).ToArray();
        var rest = _commands.CreateMoreMenu(_more).Children;
        if (spilled.Length == 0) return rest;
        return [UIMenu.Create(string.Empty, null, UIMenuIdentifier.None, UIMenuOptions.DisplayInline, spilled), .. rest];
    }

    private void Update()
    {
        if (_disposed) return;
        var commands = _commands.PrimaryCommands.OfType<MailReaderCommand>().ToArray();
        var replies = commands.Where(static command => IsReply(command.Operation)).ToArray();

        // Entries in setting order: each command, with the Reply group as one entry where its first member sits.
        var entries = new List<MailReaderCommand[]>();
        foreach (var command in commands)
        {
            if (!IsReply(command.Operation)) entries.Add([command]);
            else if (ReferenceEquals(command, replies[0])) entries.Add(replies);
        }

        for (int index = 0; index < SlotCount; index++)
        {
            var button = _slots[index];
            var entry = index < entries.Count ? entries[index] : null;
            button.Hidden = entry is null;
            _slotCommands[index] = entry is { Length: 1 } && !IsReply(entry[0].Operation) ? entry[0] : null;
            if (entry is null) continue;

            if (_slotCommands[index] is { } single)
            {
                button.ShowsMenuAsPrimaryAction = false;
                button.Menu = null;
                Apply(button, single.Glyph, single.Title, single.IsEnabled);
            }
            else
            {
                button.Menu = UIMenu.Create(entry.Select(command => (UIMenuElement)command.CreateAction(button)).ToArray());
                button.ShowsMenuAsPrimaryAction = true;
                Apply(button, WinoIconGlyph.Reply, Translator.MailOperation_Reply, entry.Any(static command => command.IsEnabled));
            }
        }
        _spilled = entries.Skip(SlotCount).SelectMany(static entry => entry).ToArray();
    }

    private static void Apply(UIButton button, WinoIconGlyph glyph, string title, bool enabled)
    {
        button.Enabled = enabled;
        if (button.Configuration is { } configuration)
        {
            configuration.Image = WinoIcons.TemplateImage(glyph, PhoneBarItems.RowGlyphSize);
            button.Configuration = configuration;
        }
        button.AccessibilityLabel = title;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _commands.CommandsChanged -= CommandsChanged;
    }
}
