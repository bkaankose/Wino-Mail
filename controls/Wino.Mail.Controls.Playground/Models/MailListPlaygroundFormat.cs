using Wino.Mail.Controls.Core;
using Wino.Mail.Controls.Core.IntelligenceTileBar;

namespace Wino.Mail.Controls.Playground.Models;

public static class MailListPlaygroundFormat
{
    public static IContactPicture ContactPicture(IMailListSourceItem item) => (IContactPicture)item;

    public static string Subject(IMailListSourceItem item) => ((MailListPlaygroundItem)item).Subject;

    public static string Sender(IMailListSourceItem item) => ((MailListPlaygroundItem)item).Sender;

    public static string Preview(IMailListSourceItem item) => ((MailListPlaygroundItem)item).Preview;

    public static IReadOnlyList<WinoIntelligenceTile> IntelligenceTiles(IMailListSourceItem item)
        => ((MailListPlaygroundItem)item).IntelligenceTiles;
}
