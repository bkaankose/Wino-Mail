using MailCal.Core.Domain.Enums;

namespace MailCal.Core.Domain.Models.Personalization;

public class IconStyleContainer
{
    public IconStyleContainer(MailCalIconStyle iconStyle, string title)
    {
        IconStyle = iconStyle;
        Title = title;
    }

    public MailCalIconStyle IconStyle { get; }
    public string Title { get; }
}
