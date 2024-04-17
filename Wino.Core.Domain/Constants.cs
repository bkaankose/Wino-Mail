namespace Wino.Core.Domain
{
    public static class Constants
    {
        /// <summary>
        /// MIME header that exists in all the drafts created from Wino.
        /// </summary>
        public const string WinoLocalDraftHeader = "X-Wino-Draft-Id";
        public const string LocalDraftStartPrefix = "localDraft_";

        public const string ToastMailItemIdKey = nameof(ToastMailItemIdKey);
        public const string ToastActionKey = nameof(ToastActionKey);
    }
}
