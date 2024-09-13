namespace Wino.Core.Domain.Enums
{
    public enum AccountCreationDialogState
    {
        Idle,
        SigningIn,
        PreparingFolders,
        Completed,
        ManuelSetupWaiting,
        TestingConnection,
        AutoDiscoverySetup,
        AutoDiscoveryInProgress,
        FetchingProfileInformation,
        Canceled
    }
}
