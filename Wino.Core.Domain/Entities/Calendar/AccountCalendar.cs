using System;
using SQLite;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.Entities.Calendar;

public class AccountCalendar : IAccountCalendar
{
    [PrimaryKey]
    public Guid Id { get; set; } = Guid.NewGuid();

    [NotNull]
    public string RemoteCalendarId { get; set; } = string.Empty;

    [NotNull]
    public Guid AccountId { get; set; }

    [NotNull]
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? Location { get; set; }

    public string? TimeZone { get; set; }

    public string? AccessRole { get; set; }

    public bool IsPrimary { get; set; } = false;

    public string? BackgroundColor { get; set; }

    public string? ForegroundColor { get; set; }

    public DateTime CreatedDate { get; set; }

    public DateTime LastModified { get; set; }

    public DateTime? LastSyncTime { get; set; }

    public string? SynchronizationDeltaToken { get; set; }

    public bool IsDeleted { get; set; } = false;
    public bool IsExtended { get; set; } = true;

    /// <summary>
    /// Unused for now.
    /// </summary>
    public string TextColorHex { get; set; }
    public string BackgroundColorHex { get; set; }
}
