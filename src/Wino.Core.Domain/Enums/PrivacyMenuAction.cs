namespace Wino.Core.Domain.Enums;

public enum PrivacyMenuAction
{
    LoadOnce,
    LoadTrackersOnce,
    BlockOnce,
    AlwaysLoadFromAddress,
    AlwaysLoadFromDomain,
    ImagesOnlyFromDomain,
    NeverLoadFromSender,
    ChangeRuleToImagesOnly,
    ChangeRuleToLoadEverything,
    RemoveRule,
    ManageRules
}
