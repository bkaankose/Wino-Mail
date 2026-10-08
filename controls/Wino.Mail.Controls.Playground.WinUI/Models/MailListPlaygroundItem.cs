using System.ComponentModel;
using System.Runtime.CompilerServices;
using Wino.Mail.Controls.Core;
using Wino.Mail.Controls.Core.IntelligenceTileBar;
using WinRT;

namespace Wino.Mail.Controls.Playground.Models;

[GeneratedBindableCustomProperty]
public sealed partial class MailListPlaygroundItem : IMailListSourceItem, IContactPicture
{
    private bool _isSelected;
    private IReadOnlyList<WinoIntelligenceTile> _intelligenceTiles = [];

    public MailListPlaygroundItem(
        string threadId,
        DateTime createdAt,
        string sender,
        string subject,
        string preview,
        string senderAddress)
    {
        Id = Guid.NewGuid();
        ThreadId = threadId;
        CreatedAt = createdAt;
        Sender = sender;
        Subject = subject;
        Preview = preview;
        SenderAddress = senderAddress;
    }

    public Guid Id { get; }
    public Guid UniqueId => Id;
    public Guid StableId => Id;
    public string ThreadId { get; }
    public string ThreadKey => ThreadId;
    public DateTime CreatedAt { get; }
    public DateTime CreationDate => CreatedAt;
    public DateTimeOffset DateSortKey => new(CreatedAt);
    public string Sender { get; }
    public string SenderAddress { get; }
    public string FromName => Sender;
    public string NameSortKey => Sender;
    public bool IsPinned => false;
    public string Subject { get; }
    public string Preview { get; }
    public string PreviewText => Preview;
    string IContactPicture.Name => Sender;
    string IContactPicture.Address => SenderAddress;
    public string? LocalImagePath => null;

    public IReadOnlyList<WinoIntelligenceTile> IntelligenceTiles
    {
        get => _intelligenceTiles;
        set
        {
            _intelligenceTiles = value ?? [];
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IntelligenceTiles)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
}
