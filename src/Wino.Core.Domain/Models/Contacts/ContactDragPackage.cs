using System;
using System.Collections.Generic;
using System.Linq;

namespace Wino.Core.Domain.Models.Contacts;

/// <summary>
/// Payload carried while contacts are dragged onto a contact list in the navigation pane.
/// </summary>
public sealed class ContactDragPackage
{
    public const string DataPropertyName = nameof(ContactDragPackage);

    public ContactDragPackage(IEnumerable<Guid> contactIds)
    {
        ContactIds = contactIds?
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList() ?? [];
    }

    public IReadOnlyList<Guid> ContactIds { get; }
}
