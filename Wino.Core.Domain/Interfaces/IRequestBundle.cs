using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Interfaces
{
    /// <summary>
    /// Represents a group of requests.
    /// </summary>
    public interface IRequestBundle
    {
        string BundleId { get; set; }
        IUIChangeRequest UIChangeRequest { get; }
    }

    /// <summary>
    /// Represents a group of requests with their native response types.
    /// </summary>
    /// <typeparam name="TRequest">Native request type from each synchronizer to store.</typeparam>
    public interface IRequestBundle<TRequest> : IRequestBundle
    {
        TRequest NativeRequest { get; }
        IRequestBase Request { get; }
    }

    public interface IRequestBase : IClientMessage, IUIChangeRequest
    {
        /// <summary>
        /// Whether synchronizations should be delayed after executing this request.
        /// Specially Outlook sometimes don't report changes back immidiately after sending the API request.
        /// This results following synchronization to miss the changes.
        /// We add small delay for the following synchronization after executing current requests to overcome this issue.
        /// Default is false.
        /// </summary>
        int ResynchronizationDelay { get; }

        object GroupingKey();
    }

    public interface IUIChangeRequest
    {
        /// <summary>
        /// UI changes to apply to the item before sending the request to the server.
        /// Changes here only affect the UI, not the item itself.
        /// Changes here are reverted if the request fails by calling <see cref="RevertUIChanges"/>.
        /// </summary>
        void ApplyUIChanges();

        /// <summary>
        /// Reverts the UI changes applied by <see cref="ApplyUIChanges"/> if the request fails.
        /// </summary>
        void RevertUIChanges();
    }

    public interface IMailActionRequest : IRequestBase
    {
        MailCopy Item { get; }
        MailSynchronizerOperation Operation { get; }
    }

    public interface IFolderActionRequest : IRequestBase
    {
        MailItemFolder Folder { get; }

        FolderSynchronizerOperation Operation { get; }
    }
}
