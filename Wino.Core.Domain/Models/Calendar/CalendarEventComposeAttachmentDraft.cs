using System;

namespace Wino.Core.Domain.Models.Calendar;

public class CalendarEventComposeAttachmentDraft
{
    public Guid Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string FileExtension { get; set; } = string.Empty;
    public long Size { get; set; }
}
