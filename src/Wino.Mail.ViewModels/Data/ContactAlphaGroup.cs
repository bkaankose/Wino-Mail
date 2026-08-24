using System.Collections.ObjectModel;

namespace Wino.Mail.ViewModels.Data;

/// <summary>
/// One alphabetical section of the contacts list. Groups are appended as pages load,
/// so incremental loading keeps working.
/// </summary>
public class ContactAlphaGroup : ObservableCollection<AccountContactViewModel>
{
    public string Key { get; }

    public ContactAlphaGroup(string key) => Key = key;
}
