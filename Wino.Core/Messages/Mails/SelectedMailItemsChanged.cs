namespace Wino.Core.Messages.Mails
{
    /// <summary>
    /// When selected mail count is changed.
    /// </summary>
    /// <param name="SelectedItemCount">New selected mail count.</param>
    public record SelectedMailItemsChanged(int SelectedItemCount);
}
