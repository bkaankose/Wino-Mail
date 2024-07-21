namespace Wino.Domain.Interfaces
{
    public interface IImapChangeProcessor : IDefaultChangeProcessor
    {
        /// <summary>
        /// Returns all known uids for the given folder.
        /// </summary>
        /// <param name="folderId">Folder id to retrieve uIds for.</param>
        Task<IList<uint>> GetKnownUidsForFolderAsync(Guid folderId);
    }
}
