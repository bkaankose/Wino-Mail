namespace Wino.Messaging.Client.Mails
{
    /// <summary>
    /// When selected mail count is changed.
    /// </summary>
    /// <param name="SelectedItemCount">New selected mail count.</param>
    public record SelectedMailItemsChanged(int SelectedItemCount);
}
