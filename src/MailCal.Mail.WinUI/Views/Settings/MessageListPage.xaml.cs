using MailCal.Views.Abstract;

namespace MailCal.Views.Settings;

public sealed partial class MessageListPage : MessageListPageAbstract
{
    public MessageListPage()
    {
        this.InitializeComponent();
    }

    protected override void ReleasePageResources()
        => ReleaseTemplateSelector("PreviewMailItemTemplateSelector");
}
