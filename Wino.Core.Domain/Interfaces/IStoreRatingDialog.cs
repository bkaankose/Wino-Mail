namespace Wino.Core.Domain.Interfaces
{
    public interface IStoreRatingDialog
    {
        bool DontAskAgain { get; }
        bool RateWinoClicked { get; }
    }
}
