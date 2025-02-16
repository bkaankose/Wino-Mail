namespace Wino.Messaging.UI
{
    /// <summary>
    /// When authenticators are proposed to copy the auth URL on the UI.
    /// </summary>
    /// <param name="AuthURL">URL to be copied to clipboard.</param>
    public record CopyAuthURLRequested(string AuthURL) : UIMessageBase<CopyAuthURLRequested>;
}
