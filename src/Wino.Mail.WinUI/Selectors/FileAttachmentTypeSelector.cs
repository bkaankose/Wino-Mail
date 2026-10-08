using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Enums;

namespace Wino.Mail.WinUI.Selectors;

public partial class FileAttachmentTypeSelector : DataTemplateSelector
{
    public DataTemplate None { get; set; } = null!;
    public DataTemplate Executable { get; set; } = null!;
    public DataTemplate Image { get; set; } = null!;
    public DataTemplate Audio { get; set; } = null!;
    public DataTemplate Video { get; set; } = null!;
    public DataTemplate PDF { get; set; } = null!;
    public DataTemplate HTML { get; set; } = null!;
    public DataTemplate RarArchive { get; set; } = null!;
    public DataTemplate Archive { get; set; } = null!;
    public DataTemplate Other { get; set; } = null!;

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
    {
        if (item == null)
            return None;

        var type = (MailAttachmentType)item;

        switch (type)
        {
            case MailAttachmentType.None:
                return None;
            case MailAttachmentType.Executable:
                return Executable;
            case MailAttachmentType.Image:
                return Image;
            case MailAttachmentType.Audio:
                return Audio;
            case MailAttachmentType.Video:
                return Video;
            case MailAttachmentType.PDF:
                return PDF;
            case MailAttachmentType.HTML:
                return HTML;
            case MailAttachmentType.RarArchive:
                return RarArchive;
            case MailAttachmentType.Archive:
                return Archive;
            default:
                return Other;
        }
    }
}
