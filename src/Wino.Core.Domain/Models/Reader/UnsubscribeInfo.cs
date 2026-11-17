namespace Wino.Core.Domain.Models.Reader;

public class UnsubscribeInfo
{
    public string HttpLink { get; set; }
    public string MailToLink { get; set; }
    public bool IsOneClick { get; set; }
    public bool CanUnsubscribe => HttpLink != null || MailToLink != null;
}
