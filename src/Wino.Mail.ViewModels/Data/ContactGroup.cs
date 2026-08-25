using System.Collections.ObjectModel;
#if WINRT_EXPOSED
using WinRT;
#endif

namespace Wino.Mail.ViewModels.Data;

/// <summary>
/// One alphabetical section of the contacts list. Native AOT requires a concrete,
/// WinRT-exposed group type: the generic toolkit groups have no generated vtable, so a
/// grouped CollectionViewSource cannot enumerate them and the list renders empty in
/// trimmed builds.
/// </summary>
#if WINRT_EXPOSED
[GeneratedWinRTExposedType]
#endif
public sealed partial class ContactGroup : ObservableCollection<AccountContactViewModel>
{
    public ContactGroup(string key) => Key = key;

    public string Key { get; }
}
