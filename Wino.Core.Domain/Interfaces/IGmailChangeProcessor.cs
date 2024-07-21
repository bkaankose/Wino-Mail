namespace Wino.Domain.Interfaces
{
    public interface IGmailChangeProcessor : IDefaultChangeProcessor
    {
        Task MapLocalDraftAsync(string mailCopyId, string newDraftId, string newThreadId);
    }
}
