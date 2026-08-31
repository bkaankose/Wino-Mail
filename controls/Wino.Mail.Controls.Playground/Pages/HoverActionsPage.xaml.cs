using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.Controls.Core;
using Wino.Mail.Controls.Core.HoverActions;

namespace Wino.Mail.Controls.Playground.Pages;

public sealed partial class HoverActionsPage : Page, INotifyPropertyChanged
{
    private string _statusText = "No action invoked";

    public HoverActionsPage()
    {
        Item = new PlaygroundHoverActionItem();
        Row = MailListRow.Single(Item);
        Labels = new("Archive", "Delete", "Flag / Unflag", "Read / Unread", "Move to Junk");
        ActionCommand = new PlaygroundCommand(OnActionInvoked);
        InitializeComponent();
    }

    public PlaygroundHoverActionItem Item { get; }

    public MailListRow Row { get; }

    public HoverActionLabels Labels { get; }

    public ICommand ActionCommand { get; }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText == value)
                return;

            _statusText = value;
            PropertyChanged?.Invoke(this, new(nameof(StatusText)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnActionInvoked(HoverActionCommandRequest request)
    {
        if (request.Action == HoverActionKind.ToggleRead)
            Item.IsRead = !Item.IsRead;
        else if (request.Action == HoverActionKind.ToggleFlag)
            Item.IsFlagged = !Item.IsFlagged;

        StatusText = $"Invoked {request.Action}";
    }

    private void ToggleReadClicked(object sender, RoutedEventArgs e) => Item.IsRead = !Item.IsRead;

    private void ToggleFlagClicked(object sender, RoutedEventArgs e) => Item.IsFlagged = !Item.IsFlagged;

    private sealed class PlaygroundCommand(Action<HoverActionCommandRequest> execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => parameter is HoverActionCommandRequest;

        public void Execute(object? parameter)
        {
            if (parameter is HoverActionCommandRequest request)
                execute(request);
        }
    }
}

public sealed class PlaygroundHoverActionItem : IMailListSourceItem, IHoverActionItem
{
    private bool _isRead;
    private bool _isFlagged;

    public Guid StableId { get; } = Guid.NewGuid();

    public string? ThreadKey => null;

    public DateTimeOffset DateSortKey => DateTimeOffset.Now;

    public string NameSortKey => "Hover action sample";

    public bool IsPinned => false;

    public bool IsRead
    {
        get => _isRead;
        set => SetProperty(ref _isRead, value);
    }

    public bool IsFlagged
    {
        get => _isFlagged;
        set => SetProperty(ref _isFlagged, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetProperty(ref bool field, bool value, [CallerMemberName] string? propertyName = null)
    {
        if (field == value)
            return;

        field = value;
        PropertyChanged?.Invoke(this, new(propertyName));
    }
}
