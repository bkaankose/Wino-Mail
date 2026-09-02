using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.Controls.Core.ContextFlyout;

namespace Wino.Mail.Controls.Playground.Pages;

public sealed partial class ContextFlyoutPage : Page
{
    private const string ReplyGlyph = "\uF176";
    private const string ReplyAllGlyph = "\uF17A";
    private const string ForwardGlyph = "\uE7AA";
    private const string MoveGlyph = "\uE7B8";
    private const string FolderGlyph = "\uE643";
    private const string DeleteGlyph = "\uEEA6";
    private const string CategoryGlyph = "\uF599";
    private const string MarkReadGlyph = "\uF522";

    public ContextFlyoutPage()
    {
        SampleCommand = new RelayCommand<object?>(parameter =>
        {
            StatusTextBlock.Text = $"Invoked: {parameter}";
        });
        DisabledCommand = new RelayCommand(() => { }, () => false);

        HeaderItems = CreateHeaderItems();
        CardItems = CreateCardItems();
        BoundItems = CreateBoundItems();

        InitializeComponent();
    }

    public RelayCommand<object?> SampleCommand { get; }

    public RelayCommand DisabledCommand { get; }

    public IReadOnlyList<ContextFlyoutHeaderEntry> HeaderItems { get; }

    public IReadOnlyList<ContextFlyoutMenuEntry> CardItems { get; }

    public IReadOnlyList<ContextFlyoutMenuEntry> BoundItems { get; }

    private ContextFlyoutHeaderEntry[] CreateHeaderItems() =>
    [
        new ContextFlyoutHeaderEntry
        {
            Label = "Reply",
            Icon = new ContextFlyoutIcon(ReplyGlyph),
            Command = SampleCommand,
            CommandParameter = "Header reply",
            Shortcut = new ContextFlyoutShortcut("Ctrl+R", "R", Control: true),
            AutomationId = "ContextFlyoutHeaderReply"
        },
        new ContextFlyoutHeaderEntry
        {
            Label = "Reply all",
            Icon = new ContextFlyoutIcon(ReplyAllGlyph),
            Command = SampleCommand,
            CommandParameter = "Header reply all",
            AutomationId = "ContextFlyoutHeaderReplyAll"
        },
        new ContextFlyoutHeaderEntry
        {
            Label = "Unavailable",
            Icon = new ContextFlyoutIcon(ForwardGlyph),
            Command = DisabledCommand,
            IsEnabled = false,
            AutomationId = "ContextFlyoutHeaderDisabled"
        }
    ];

    private ContextFlyoutMenuEntry[] CreateCardItems() =>
    [
        // Leading separator: the flyout drops it when the page is projected.
        ContextFlyoutSeparatorEntry.Instance,
        new ContextFlyoutCommandEntry
        {
            Text = "Reply",
            Icon = new ContextFlyoutIcon(ReplyGlyph),
            Command = SampleCommand,
            CommandParameter = "Reply",
            Shortcut = new ContextFlyoutShortcut("Ctrl+R", "R", Control: true),
            AutomationId = "ContextFlyoutReplyItem"
        },
        new ContextFlyoutCommandEntry
        {
            Text = "Mark as read",
            Command = SampleCommand,
            CommandParameter = "Mark as read",
            AutomationId = "ContextFlyoutNoIconItem"
        },
        new ContextFlyoutSubMenuEntry
        {
            Text = "Move to",
            SearchKeywords = "folder destination",
            Icon = new ContextFlyoutIcon(MoveGlyph),
            AutomationId = "ContextFlyoutMoveSubItem",
            Items = new ContextFlyoutMenuEntry[]
            {
                new ContextFlyoutSubMenuEntry
                {
                    Text = "Projects",
                    Icon = new ContextFlyoutIcon(FolderGlyph),
                    AutomationId = "ContextFlyoutProjectsSubItem",
                    Items = new ContextFlyoutMenuEntry[]
                    {
                        new ContextFlyoutCommandEntry
                        {
                            Text = "Wino",
                            Icon = new ContextFlyoutIcon(FolderGlyph),
                            Command = SampleCommand,
                            CommandParameter = "Move to Wino",
                            AutomationId = "ContextFlyoutMoveToWino"
                        }
                    }
                },
                new ContextFlyoutCommandEntry
                {
                    Text = "Archive",
                    Icon = new ContextFlyoutIcon(FolderGlyph),
                    Command = SampleCommand,
                    CommandParameter = "Move to Archive",
                    AutomationId = "ContextFlyoutMoveToArchive"
                }
            }
        },
        ContextFlyoutSeparatorEntry.Instance,
        // Duplicate separator: also dropped, so callers never have to normalize.
        ContextFlyoutSeparatorEntry.Instance,
        new ContextFlyoutToggleEntry
        {
            Text = "Work",
            SearchKeywords = "category",
            Icon = new ContextFlyoutIcon(CategoryGlyph, "#FF8A3FFC"),
            IsChecked = true,
            Command = SampleCommand,
            CommandParameter = "Category Work",
            AutomationId = "ContextFlyoutCategoryWork"
        },
        new ContextFlyoutToggleEntry
        {
            Text = "Personal",
            SearchKeywords = "category",
            Icon = new ContextFlyoutIcon(CategoryGlyph, "#FF1DB954"),
            Command = SampleCommand,
            CommandParameter = "Category Personal",
            AutomationId = "ContextFlyoutCategoryPersonal"
        },
        ContextFlyoutSeparatorEntry.Instance,
        new ContextFlyoutRadioEntry
        {
            Text = "Sort by date",
            GroupName = "Sorting",
            IsChecked = true,
            Command = SampleCommand,
            CommandParameter = "Sort by date",
            AutomationId = "ContextFlyoutSortDate"
        },
        new ContextFlyoutRadioEntry
        {
            Text = "Sort by sender",
            GroupName = "Sorting",
            Command = SampleCommand,
            CommandParameter = "Sort by sender",
            AutomationId = "ContextFlyoutSortSender"
        },
        ContextFlyoutSeparatorEntry.Instance,
        new ContextFlyoutCommandEntry
        {
            Text = "Delete",
            Icon = new ContextFlyoutIcon(DeleteGlyph),
            IsDestructive = true,
            Command = SampleCommand,
            CommandParameter = "Delete",
            Shortcut = new ContextFlyoutShortcut("Delete"),
            AutomationId = "ContextFlyoutDeleteItem"
        },
        new ContextFlyoutCommandEntry
        {
            Text = "Unavailable command",
            Icon = new ContextFlyoutIcon(MarkReadGlyph),
            Command = DisabledCommand,
            IsEnabled = false,
            AutomationId = "ContextFlyoutDisabledItem"
        },
        // Trailing separator: dropped as well.
        ContextFlyoutSeparatorEntry.Instance
    ];

    private ContextFlyoutMenuEntry[] CreateBoundItems()
    {
        var folders = new List<ContextFlyoutMenuEntry>();

        for (var index = 1; index <= 24; index++)
        {
            folders.Add(new ContextFlyoutCommandEntry
            {
                Text = $"Folder {index:00}",
                SearchKeywords = "folder destination",
                Icon = new ContextFlyoutIcon(FolderGlyph),
                Command = SampleCommand,
                CommandParameter = $"Folder {index:00}",
                AutomationId = $"BoundFolder{index:00}"
            });
        }

        return
        [
            new ContextFlyoutSubMenuEntry
            {
                Text = "Move to",
                SearchKeywords = "folder destination",
                Icon = new ContextFlyoutIcon(MoveGlyph),
                Items = folders,
                AutomationId = "BoundMoveSubItem"
            }
        ];
    }
}
