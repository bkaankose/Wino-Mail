using System.Threading.Tasks;

namespace Wino.Domain.Interfaces
{
    public interface IStoreRatingService
    {
        Task PromptRatingDialogAsync();
        Task LaunchStorePageForReviewAsync();
    }
}
