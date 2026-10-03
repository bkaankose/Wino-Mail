using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Entities.Mail;

namespace Wino.Mail.ViewModels.Data;

/// <summary>
/// One checkable category in the contact editor.
/// </summary>
public partial class ContactCategoryMembershipViewModel : ObservableObject
{
    public MailCategory Category { get; }
    public string Name => Category.Name;
    public string BackgroundColorHex => Category.BackgroundColorHex;

    [ObservableProperty] public partial bool IsMember { get; set; }

    public ContactCategoryMembershipViewModel(MailCategory category, bool isMember)
    {
        Category = category;
        IsMember = isMember;
    }
}
