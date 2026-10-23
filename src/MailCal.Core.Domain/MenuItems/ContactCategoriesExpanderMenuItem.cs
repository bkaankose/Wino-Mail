using System;
using System.Threading.Tasks;

namespace MailCal.Core.Domain.MenuItems;

/// <summary>
/// The "Categories" entry of the contacts pane. Invoking it shows or hides the categories
/// of the active account below it; <see cref="MenuItemBase.IsExpanded"/> holds that state.
/// </summary>
public sealed class ContactCategoriesExpanderMenuItem(string title) : MenuItemBase
{
    public string Title { get; } = title;

    /// <summary>Creates a category for the active account from the entry's context menu.</summary>
    public Func<Task> NewCategoryRequested { get; set; }
}
