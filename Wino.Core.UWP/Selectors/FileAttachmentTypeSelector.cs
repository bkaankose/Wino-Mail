using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Core.Domain.Enums;

namespace Wino.Core.UWP.Selectors;

public partial class FileAttachmentTypeSelector : DataTemplateSelector
{
    public DataTemplate None { get; set; }
    public DataTemplate Executable { get; set; }
    public DataTemplate Image { get; set; }
    public DataTemplate Audio { get; set; }
    public DataTemplate Video { get; set; }
    public DataTemplate PDF { get; set; }
    public DataTemplate HTML { get; set; }
    public DataTemplate RarArchive { get; set; }
    public DataTemplate Archive { get; set; }
    public DataTemplate Other { get; set; }

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
