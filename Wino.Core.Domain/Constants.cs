using Wino.Domain.Enums;

namespace Wino.Domain
{
    public static class Constants
    {
        /// <summary>
        /// MIME header that exists in all the drafts created from Wino.
        /// </summary>
        public const string WinoLocalDraftHeader = "X-Wino-Draft-Id";
        public const string LocalDraftStartPrefix = "localDraft_";

        // Toast Notification Keys
        public const string ToastMailItemIdKey = nameof(ToastMailItemIdKey);
        public const string ToastMailItemRemoteFolderIdKey = nameof(ToastMailItemRemoteFolderIdKey);
        public const string ToastActionKey = nameof(ToastActionKey);

        // App Configuration
        public const AppLanguage DefaultAppLanguage = AppLanguage.English;
        public const string SharedFolderName = "WinoShared";
        public static char MailCopyUidSeparator = '_';

        // GMail Category Labels
        public const string FORUMS_LABEL_ID = "FORUMS";
        public const string UPDATES_LABEL_ID = "UPDATES";
        public const string PROMOTIONS_LABEL_ID = "PROMOTIONS";
        public const string SOCIAL_LABEL_ID = "SOCIAL";
        public const string PERSONAL_LABEL_ID = "PERSONAL";

        public static string[] SubCategoryFolderLabelIds =
        [
            FORUMS_LABEL_ID,
            UPDATES_LABEL_ID,
            PROMOTIONS_LABEL_ID,
            SOCIAL_LABEL_ID,
            PERSONAL_LABEL_ID
        ];

        // File Names

        public const string ProtocolLogFileName = "ImapProtocolLog.log";
        public const string WinoLogFileName = "WinoDiagnostics.log";
    }
}
