namespace Wino.Core.Domain
{
    public static class Constants
    {
        /// <summary>
        /// MIME header that exists in all the drafts created from Wino.
        /// </summary>
        public const string WinoLocalDraftHeader = "X-Wino-Draft-Id";
        public const string LocalDraftStartPrefix = "localDraft_";

        public const string ToastMailUniqueIdKey = nameof(ToastMailUniqueIdKey);
        public const string ToastActionKey = nameof(ToastActionKey);

        public const string ClientLogFile = "Client_.log";
        public const string ServerLogFile = "Server_.log";
    }
}
