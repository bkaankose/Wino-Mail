#nullable enable

using System.Collections.Generic;
using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// A menu item that can accept dragged payloads. Lets navigation item templates declare
/// drag and drop without any shell code-behind, through the drop behavior in the view layer.
/// </summary>
public interface IMenuItemDropTarget
{
    /// <summary>
    /// Highlight state while a compatible payload hovers over the item.
    /// </summary>
    bool IsDraggingItemOver { get; set; }

    bool CanAccept(IReadOnlyDictionary<string, object> dataProperties);

    /// <summary>
    /// Caption shown in the drag UI override. Only called when <see cref="CanAccept"/> passed.
    /// </summary>
    string GetDropCaption(IReadOnlyDictionary<string, object> dataProperties);

    Task HandleDropAsync(IReadOnlyDictionary<string, object> dataProperties);
}
