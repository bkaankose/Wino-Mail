using System;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.ComponentModel;
#if WINRT_EXPOSED
using WinRT;
#endif

namespace Wino.Mail.ViewModels.Data;

/// <summary>
/// One stable section of the task list. Smart views group tasks by account; named lists use one
/// headerless group.
/// Native AOT requires a concrete, WinRT-exposed group type: the generic toolkit groups have no
/// generated vtable, so a grouped CollectionViewSource cannot enumerate them and the list renders
/// empty in trimmed builds. Mirrors <see cref="ContactGroup"/>.
/// </summary>
#if WINRT_EXPOSED
[GeneratedWinRTExposedType]
#endif
public sealed partial class TaskGroup : ObservableCollection<TaskItemViewModel>
{
    public TaskGroup(string key, Guid? accountId, bool showHeader)
    {
        Key = key;
        AccountId = accountId;
        ShowHeader = showHeader;
    }

    public string Key { get; private set; }
    public Guid? AccountId { get; }
    public bool ShowHeader { get; }

    public string HeaderCountText => Count.ToString();

    public void UpdateHeader(string key)
    {
        if (string.Equals(Key, key, StringComparison.Ordinal))
            return;

        Key = key;
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Key)));
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        base.OnCollectionChanged(e);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(HeaderCountText)));
    }
}
