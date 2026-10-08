using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Mail.ViewModels.Data;

/// <summary>
/// One checkable list row in the contact editor.
/// </summary>
public partial class ContactListMembershipViewModel : ObservableObject
{
    public ContactList List { get; }
    public Guid ListId => List.Id;
    public string Name => List.Name;

    [ObservableProperty] public partial bool IsMember { get; set; }

    public ContactListMembershipViewModel(ContactList list, bool isMember)
    {
        List = list;
        IsMember = isMember;
    }
}
