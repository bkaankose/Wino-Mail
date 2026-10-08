namespace Wino.Core.Domain.Enums;

public enum AccountCreationDialogState
{
    Idle,
    SigningIn,
    PreparingFolders,
    CalendarMetadataFetch,
    Completed,
    ManuelSetupWaiting,
    TestingConnection,
    AutoDiscoverySetup,
    AutoDiscoveryInProgress,
    FetchingProfileInformation,
    Canceled,
    FetchingEvents
}
