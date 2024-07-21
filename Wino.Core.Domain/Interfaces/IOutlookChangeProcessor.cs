namespace Wino.Domain.Interfaces
{
    public interface IOutlookChangeProcessor : IDefaultChangeProcessor
    {
        /// <summary>
        /// Interrupted initial synchronization may cause downloaded mails to be saved in the database twice.
        /// Since downloading mime is costly in Outlook, we need to check if the actual copy of the message has been saved before.
        /// </summary>
        /// <param name="messageId">MailCopyId of the message.</param>
        /// <returns>Whether the mime has b</returns>
        Task<bool> IsMailExistsAsync(string messageId);

        /// <summary>
        /// Updates Folder's delta synchronization identifier.
        /// Only used in Outlook since it does per-folder sync.
        /// </summary>
        /// <param name="folderId">Folder id</param>
        /// <param name="synchronizationIdentifier">New synchronization identifier.</param>
        /// <returns>New identifier if success.</returns>
        Task UpdateFolderDeltaSynchronizationIdentifierAsync(Guid folderId, string deltaSynchronizationIdentifier);
    }
}
