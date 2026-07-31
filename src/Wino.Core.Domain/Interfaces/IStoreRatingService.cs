using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces;

public interface IStoreRatingService
{
    Task PromptRatingDialogAsync();
    Task LaunchStorePageForReviewAsync();
}
