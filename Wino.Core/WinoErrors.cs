namespace Wino.Core
{
    /// <summary>
    /// Error codes for Wino application.
    /// Pretty outdated.
    /// </summary>
    public static class WinoErrors
    {
        public const string AccountStructureRender = nameof(AccountStructureRender);
        public const string MimeRendering = nameof(MimeRendering);
        public const string MailRendering = nameof(MailRendering);
        public const string FolderOperationExecution = nameof(FolderOperationExecution);
        public const string StartupAccountExtendFail = nameof(StartupAccountExtendFail);
        public const string AccountNavigateInboxFail = nameof(AccountNavigateInboxFail);
        public const string AccountCreation = nameof(AccountCreation);

        public const string OutlookIntegratorFolderSync = nameof(OutlookIntegratorFolderSync);
        public const string GoogleSynchronizerAccountSync = nameof(GoogleSynchronizerAccountSync);
        public const string ImapFolderSync = nameof(ImapFolderSync);

        public const string RendererCommandMailOperation = nameof(RendererCommandMailOperation);
        public const string MailListingMailOperation = nameof(MailListingMailOperation);

        public const string AutoMarkAsRead = nameof(AutoMarkAsRead);
        public const string MailListGetItem = nameof(MailListGetItem);
        public const string MailListCollectionUpdate = nameof(MailListCollectionUpdate);
        public const string MailListRefreshFolder = nameof(MailListRefreshFolder);
        public const string ProcessorTaskFailed = nameof(ProcessorTaskFailed);
        public const string SearchFailed = nameof(SearchFailed);

        public const string BatchExecutionFailed = nameof(BatchExecutionFailed);
        public const string SingleBatchExecutionFailedGoogle = nameof(SingleBatchExecutionFailedGoogle);

        public const string SynchronizationWorkerException = nameof(SynchronizationWorkerException);
        public const string StoreRatingSubmission = nameof(StoreRatingSubmission);

        public const string OpenAttachment = nameof(OpenAttachment);
        public const string SaveAttachment = nameof(SaveAttachment);

        public const string OutlookMimeSaveFailure = nameof(OutlookMimeSaveFailure);
    }
}
