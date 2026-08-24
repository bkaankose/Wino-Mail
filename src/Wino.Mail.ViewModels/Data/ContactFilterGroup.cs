using System.Collections.ObjectModel;

namespace Wino.Mail.ViewModels.Data;

/// <summary>
/// One titled section of the contacts sidebar. Section titles come from grouping so
/// that they are never selectable.
/// </summary>
public class ContactFilterGroup : ObservableCollection<ContactFilterViewModel>
{
    public string Title { get; }
    public bool HasTitle => !string.IsNullOrWhiteSpace(Title);

    public ContactFilterGroup(string title) => Title = title;
}
