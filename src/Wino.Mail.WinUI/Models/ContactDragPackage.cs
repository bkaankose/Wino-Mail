using System;
using System.Collections.Generic;
using System.Linq;

namespace Wino.Mail.WinUI.Models;

internal sealed class ContactDragPackage
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
