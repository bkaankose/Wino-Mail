using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.MenuItems;

/// <summary>
/// A node of a read-only remote tree (Exchange public folders or the online archive) in the mail navigation.
/// Wraps a synthetic, never-persisted <see cref="MailItemFolder"/>. The tree is walked lazily: a placeholder
/// child gives a node its expander, and the first expansion raises <see cref="ChildrenRequested"/> so the
/// shell can replace the placeholder with the folder's real children, fetched live.
/// </summary>
public partial class RemoteFolderMenuItem : FolderMenuItem
{
    /// <summary>Raised on the first expansion of a node whose children have not been loaded yet.</summary>
    public event EventHandler ChildrenRequested;

    /// <summary>Raised when the user asks to pin or unpin this public folder.</summary>
    public event EventHandler PinToggleRequested;

    /// <summary>Whether this public folder is currently pinned under the account's folders.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinActionText))]
    public partial bool IsPinned { get; set; }

    public RemoteFolderMenuItem(MailItemFolder folder, MailAccount parentAccount, IMenuItem parentMenuItem)
        : base(folder, parentAccount, parentMenuItem)
    {
    }

    public MailItemFolder RemoteFolder => (MailItemFolder)Parameter;

    public PublicFolderKind Kind => RemoteFolder.PublicFolderKind;

    public bool IsOnlineArchive => RemoteFolder.IsOnlineArchiveNode;

    /// <summary>A "Loading" or informational row: inert, never navigated, never expanded.</summary>
    public bool IsPlaceholder => RemoteFolder.IsPublicFolderPlaceholder;

    /// <summary>The tree root ("Public Folders" or "Online Archive"), which has no remote id of its own.</summary>
    public bool IsRoot => !IsPlaceholder && string.IsNullOrEmpty(RemoteFolder.RemoteFolderId);

    /// <summary>A pinned quick-access entry shown under the account's folders rather than inside the tree.</summary>
    public bool IsPinnedEntry { get; init; }

    /// <summary>Whether the real children replaced the placeholder (or the node was created without one).</summary>
    public bool AreChildrenLoaded { get; private set; }

    /// <summary>Only mail folders open in the mail list; containers and other kinds are structure only.</summary>
    public bool CanOpen => !IsPlaceholder && !IsRoot && Kind == PublicFolderKind.Mail;

    /// <summary>
    /// Public mail, contact and calendar folders can be pinned. A mail folder then shows under the account's
    /// folders, a contact folder in People and a calendar folder in Calendar; other kinds have no home.
    /// </summary>
    public bool CanPin => !IsPlaceholder && !IsRoot && !IsOnlineArchive &&
                          Kind is PublicFolderKind.Mail or PublicFolderKind.Contacts or PublicFolderKind.Calendar;

    public string PinActionText => Kind switch
    {
        _ when IsPinned => Translator.PublicFolders_Unpin,
        PublicFolderKind.Contacts => Translator.PublicFolders_PinToPeople,
        PublicFolderKind.Calendar => Translator.PublicFolders_PinToCalendar,
        _ => Translator.PublicFolders_PinToFolders
    };

    public void MarkChildrenLoaded() => AreChildrenLoaded = true;

    [RelayCommand]
    private void TogglePin() => PinToggleRequested?.Invoke(this, EventArgs.Empty);

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.PropertyName == nameof(IsExpanded) && IsExpanded && !AreChildrenLoaded && !IsPlaceholder)
        {
            ChildrenRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
