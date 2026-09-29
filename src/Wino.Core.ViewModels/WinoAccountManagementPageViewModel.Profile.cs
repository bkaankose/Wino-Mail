#nullable enable
using System;
using System.IO;
using System.Threading.Tasks;
using System.Threading;
using Wino.Core.Domain.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Exceptions;
using Wino.Mail.Api.Contracts.Auth;
using Wino.Mail.Api.Contracts.Common;

namespace Wino.Core.ViewModels;

public partial class WinoAccountManagementPageViewModel
{
    private Guid? _profileAccountId;
    private WinoAccountSession? _profileEditorSession;
    private Guid? _avatarRevision;

    [ObservableProperty]
    public partial string AccountDisplayName { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveProfileNameCommand))]
    public partial string ProfileNameDraft { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? AccountAvatarPath { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UploadProfilePhotoCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelProfilePhotoCommand))]
    public partial byte[]? ProfilePhotoDraft { get; set; }

    [ObservableProperty]
    public partial string ProfileMessage { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveProfileNameCommand))]
    [NotifyCanExecuteChangedFor(nameof(ChooseProfilePhotoCommand))]
    [NotifyCanExecuteChangedFor(nameof(UploadProfilePhotoCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveProfilePhotoCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelProfilePhotoCommand))]
    public partial bool IsProfileBusy { get; set; }

    private bool CanEditProfile() => IsSignedIn && _profileAccountId is not null && !IsProfileBusy;
    private bool CanSaveProfileName() => CanEditProfile() && ProfileNameDraft != AccountDisplayName;
    private bool CanUploadProfilePhoto() => CanEditProfile() && ProfilePhotoDraft is not null;
    private bool CanRemoveProfilePhoto() => CanEditProfile() && _avatarRevision is not null;

    [RelayCommand(CanExecute = nameof(CanSaveProfileName))]
    private Task SaveProfileNameAsync()
        => EditProfileAsync(token => _profileService.UpdateProfileAsync(ProfileNameDraft, token), false);

    [RelayCommand(CanExecute = nameof(CanUploadProfilePhoto))]
    private Task UploadProfilePhotoAsync()
        => EditProfileAsync(token => _profileService.UploadAvatarAsync(ProfilePhotoDraft!, token), true);

    [RelayCommand(CanExecute = nameof(CanRemoveProfilePhoto))]
    private Task RemoveProfilePhotoAsync()
        => EditProfileAsync(token => _profileService.DeleteAvatarAsync(token), true);

    [RelayCommand(CanExecute = nameof(CanUploadProfilePhoto))]
    private void CancelProfilePhoto() => ProfilePhotoDraft = null;

    [RelayCommand(CanExecute = nameof(CanEditProfile))]
    private async Task ChooseProfilePhotoAsync()
    {
        var session = _profileEditorSession;
        if (_sessions is not null && (session is null || !await _sessions.IsCurrentAsync(session))) return;
        IsProfileBusy = true;
        ProfileMessage = string.Empty;
        try
        {
            var files = await _dialogService.PickFilesMetadataAsync(".jpg", ".jpeg", ".png");
            if (files.Count == 0) return;
            if (files.Count != 1 || files[0].Size > 5 * 1024 * 1024)
                throw new WinoAccountApiException(ApiErrorCodes.AvatarTooLarge);
            // Check the open stream as well, because the file may change after the picker closes.
            await using var stream = File.OpenRead(files[0].FullFilePath);
            if (stream.Length > 5 * 1024 * 1024) throw new WinoAccountApiException(ApiErrorCodes.AvatarTooLarge);
            var bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes);
            var preview = await _profileService.PrepareAvatarAsync(bytes);
            await ApplySessionUIAsync(session, () => ProfilePhotoDraft = preview);
        }
        catch (Exception exception)
        {
            _logger?.CaptureException(exception, nameof(ChooseProfilePhotoAsync));
            await ApplySessionUIAsync(session, () => ProfileMessage = ProfileError(exception.Message));
        }
        finally { await ApplySessionUIAsync(session, () => IsProfileBusy = false); }
    }

    private async Task EditProfileAsync(Func<CancellationToken, Task<ApiEnvelope<AuthUserDto>>> operation, bool photo)
    {
        var session = _profileEditorSession;
        if (_sessions is not null && (session is null || !await _sessions.IsCurrentAsync(session))) return;
        IsProfileBusy = true;
        ProfileMessage = string.Empty;
        try
        {
            var response = await operation(session?.CancellationToken ?? default);
            if (!response.IsSuccess || response.Result is null)
                throw new WinoAccountApiException(response.ErrorCode ?? ApiErrorCodes.ValidationFailed);
            await ApplySessionUIAsync(session, () =>
            {
                if (photo) ProfilePhotoDraft = null;
                else ProfileNameDraft = response.Result.DisplayName ?? response.Result.Email;
                ProfileMessage = Translator.WinoAccount_Profile_Saved;
            });
        }
        catch (Exception exception)
        {
            _logger?.CaptureException(exception, nameof(EditProfileAsync));
            await ApplySessionUIAsync(session, () => ProfileMessage = ProfileError(exception.Message));
        }
        finally { await ApplySessionUIAsync(session, () => IsProfileBusy = false); }
    }

    private static string ProfileError(string code) => code switch
    {
        ApiErrorCodes.AvatarInvalid => Translator.WinoAccount_Profile_InvalidPhoto,
        ApiErrorCodes.AvatarTooLarge => Translator.WinoAccount_Profile_PhotoLimit,
        _ => Translator.WinoAccount_Profile_SaveFailed
    };

    private void ApplyProfileEditor(WinoAccount? account, WinoAccountSession? session = null)
    {
        var name = string.IsNullOrWhiteSpace(account?.DisplayName) ? account?.Email ?? string.Empty : account.DisplayName;
        if (_profileAccountId != account?.Id || _profileEditorSession?.Generation != session?.Generation)
        {
            ProfilePhotoDraft = null;
            ProfileMessage = string.Empty;
            IsProfileBusy = false;
            ProfileNameDraft = name;
            AccountAvatarPath = null;
        }
        else if (ProfileNameDraft == AccountDisplayName) ProfileNameDraft = name;
        if (_avatarRevision != account?.AvatarRevision) AccountAvatarPath = null;
        _profileEditorSession = session;
        _profileAccountId = account?.Id;
        _avatarRevision = account?.AvatarRevision;
        AccountDisplayName = name;
        SaveProfileNameCommand.NotifyCanExecuteChanged();
        ChooseProfilePhotoCommand.NotifyCanExecuteChanged();
        UploadProfilePhotoCommand.NotifyCanExecuteChanged();
        RemoveProfilePhotoCommand.NotifyCanExecuteChanged();
        CancelProfilePhotoCommand.NotifyCanExecuteChanged();
    }
}
