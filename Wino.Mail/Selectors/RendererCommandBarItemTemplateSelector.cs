using Wino.Domain.Models.Menus;
using Wino.Domain.Enums;



#if NET8_0
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
#else
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
#endif

namespace Wino.Selectors
{
    public class RendererCommandBarItemTemplateSelector : DataTemplateSelector
    {
        public DataTemplate Reply { get; set; }
        public DataTemplate ReplyAll { get; set; }
        public DataTemplate Archive { get; set; }
        public DataTemplate Unarchive { get; set; }
        public DataTemplate SetFlag { get; set; }
        public DataTemplate ClearFlag { get; set; }
        public DataTemplate MarkAsRead { get; set; }
        public DataTemplate MarkAsUnread { get; set; }
        public DataTemplate Delete { get; set; }
        public DataTemplate Move { get; set; }
        public DataTemplate MoveToJunk { get; set; }
        public DataTemplate SaveAs { get; set; }
        public DataTemplate Zoom { get; set; }
        public DataTemplate Forward { get; set; }
        public DataTemplate DarkEditor { get; set; }
        public DataTemplate LightEditor { get; set; }
        public DataTemplate SeperatorTemplate { get; set; }
        public DataTemplate Find { get; set; }
        public DataTemplate Print { get; set; }
        protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
        {
            if (item is MailOperationMenuItem mailOperationItem)
            {
                switch (mailOperationItem.Operation)
                {
                    case MailOperation.None:
                        break;
                    case MailOperation.Archive:
                        return Archive;
                    case MailOperation.UnArchive:
                        return Unarchive;
                    case MailOperation.SoftDelete:
                        return Delete;
                    case MailOperation.Move:
                        return Move;
                    case MailOperation.MoveToJunk:
                        return MoveToJunk;
                    case MailOperation.SetFlag:
                        return SetFlag;
                    case MailOperation.ClearFlag:
                        return ClearFlag;
                    case MailOperation.MarkAsRead:
                        return MarkAsRead;
                    case MailOperation.MarkAsUnread:
                        return MarkAsUnread;
                    case MailOperation.Reply:
                        return Reply;
                    case MailOperation.ReplyAll:
                        return ReplyAll;
                    case MailOperation.Zoom:
                        return Zoom;
                    case MailOperation.SaveAs:
                        return SaveAs;
                    case MailOperation.Find:
                        return Find;
                    case MailOperation.Forward:
                        return Forward;
                    case MailOperation.DarkEditor:
                        return DarkEditor;
                    case MailOperation.LightEditor:
                        return LightEditor;
                    case MailOperation.Seperator:
                        return SeperatorTemplate;
                    case MailOperation.Print:
                        return Print;
                    default:
                        break;
                }
            }

            return base.SelectTemplateCore(item, container);
        }
    }
}
