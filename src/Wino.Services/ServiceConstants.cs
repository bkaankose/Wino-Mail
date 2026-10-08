using System.Collections.Generic;
using Wino.Core.Domain.Enums;

namespace Wino.Services;

public static class ServiceConstants
{
    #region Gmail Constants

    public const string INBOX_LABEL_ID = "INBOX";
    public const string UNREAD_LABEL_ID = "UNREAD";
    public const string IMPORTANT_LABEL_ID = "IMPORTANT";
    public const string STARRED_LABEL_ID = "STARRED";
    public const string DRAFT_LABEL_ID = "DRAFT";
    public const string SENT_LABEL_ID = "SENT";
    public const string SPAM_LABEL_ID = "SPAM";
    public const string CHAT_LABEL_ID = "CHAT";
    public const string TRASH_LABEL_ID = "TRASH";
    public const string ARCHIVE_LABEL_ID = "ARCHIVE";

    // Category labels.
    public const string FORUMS_LABEL_ID = "FORUMS";
    public const string UPDATES_LABEL_ID = "UPDATES";
    public const string PROMOTIONS_LABEL_ID = "PROMOTIONS";
    public const string SOCIAL_LABEL_ID = "SOCIAL";
    public const string PERSONAL_LABEL_ID = "PERSONAL";

    // Label visibility identifiers.
    public const string SYSTEM_FOLDER_IDENTIFIER = "system";
    public const string FOLDER_HIDE_IDENTIFIER = "labelHide";

    public const string CATEGORY_PREFIX = "CATEGORY_";
    public const string FOLDER_SEPERATOR_STRING = "/";
    public const char FOLDER_SEPERATOR_CHAR = '/';

    public static Dictionary<string, SpecialFolderType> KnownFolderDictionary = new Dictionary<string, SpecialFolderType>()
    {
        { INBOX_LABEL_ID, SpecialFolderType.Inbox },
        { CHAT_LABEL_ID, SpecialFolderType.Chat },
        { IMPORTANT_LABEL_ID, SpecialFolderType.Important },
        { TRASH_LABEL_ID, SpecialFolderType.Deleted },
        { DRAFT_LABEL_ID, SpecialFolderType.Draft },
        { SENT_LABEL_ID, SpecialFolderType.Sent },
        { SPAM_LABEL_ID, SpecialFolderType.Junk },
        { STARRED_LABEL_ID, SpecialFolderType.Starred },
        { UNREAD_LABEL_ID, SpecialFolderType.Unread },
        { FORUMS_LABEL_ID, SpecialFolderType.Forums },
        { UPDATES_LABEL_ID, SpecialFolderType.Updates },
        { PROMOTIONS_LABEL_ID, SpecialFolderType.Promotions },
        { SOCIAL_LABEL_ID, SpecialFolderType.Social},
        { PERSONAL_LABEL_ID, SpecialFolderType.Personal},
    };

    public static string[] SubCategoryFolderLabelIds =
    [
        FORUMS_LABEL_ID,
        UPDATES_LABEL_ID,
        PROMOTIONS_LABEL_ID,
        SOCIAL_LABEL_ID,
        PERSONAL_LABEL_ID
    ];

    #endregion
}
