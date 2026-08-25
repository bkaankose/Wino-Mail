using System.Collections.ObjectModel;
#if WINRT_EXPOSED
using WinRT;
#endif

namespace Wino.Mail.ViewModels.Data;

/// <summary>
/// One section of the task list — the open tasks, or the collapsed "Completed" group.
/// Native AOT requires a concrete, WinRT-exposed group type: the generic toolkit groups have no
/// generated vtable, so a grouped CollectionViewSource cannot enumerate them and the list renders
/// empty in trimmed builds. Mirrors <see cref="ContactGroup"/>.
/// </summary>
#if WINRT_EXPOSED
[GeneratedWinRTExposedType]
#endif
public sealed partial class TaskGroup : ObservableCollection<TaskItemViewModel>
{
    public TaskGroup(string key, bool isCompletedGroup)
    {
        Key = key;
        IsCompletedGroup = isCompletedGroup;
    }

    public string Key { get; }

    /// <summary>The completed group renders a collapsible header; the active group has none.</summary>
    public bool IsCompletedGroup { get; }

    public string HeaderCountText => Count.ToString();
}
