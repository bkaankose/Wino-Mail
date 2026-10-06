using System.ComponentModel;
using System.Runtime.CompilerServices;
using Wino.Mail.Controls.Core.AccountIcon;

namespace Wino.Mail.Controls.Playground.ViewModels;

public sealed class AccountIconPageViewModel : INotifyPropertyChanged
{
    private AccountIconProviderOption _selectedProvider;
    private string _accountColorHex = "#4F6BED";
    private string? _profilePicturePath;
    private bool _isProfilePictureEnabled = true;
    private AccountIconInfo _account;

    public AccountIconPageViewModel()
    {
        ProviderOptions =
        [
            new AccountIconProviderOption(AccountIconProvider.Microsoft, "Microsoft"),
            new AccountIconProviderOption(AccountIconProvider.Google, "Google"),
            new AccountIconProviderOption(AccountIconProvider.ICloud, "iCloud"),
            new AccountIconProviderOption(AccountIconProvider.Yahoo, "Yahoo"),
            new AccountIconProviderOption(AccountIconProvider.Imap, "IMAP"),
        ];
        _selectedProvider = ProviderOptions[0];
        _account = CreateAccount();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AccountIconProviderOption[] ProviderOptions { get; }

    public AccountIconProviderOption SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (Equals(_selectedProvider, value))
            {
                return;
            }

            _selectedProvider = value;
            OnPropertyChanged();
            RefreshAccount();
        }
    }

    public string AccountColorHex
    {
        get => _accountColorHex;
        set
        {
            if (string.Equals(_accountColorHex, value, StringComparison.Ordinal))
            {
                return;
            }

            _accountColorHex = value;
            OnPropertyChanged();
            RefreshAccount();
        }
    }

    public bool IsProfilePictureEnabled
    {
        get => _isProfilePictureEnabled;
        set
        {
            if (_isProfilePictureEnabled == value)
            {
                return;
            }

            _isProfilePictureEnabled = value;
            OnPropertyChanged();
        }
    }

    public AccountIconInfo Account
    {
        get => _account;
        private set
        {
            _account = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProfilePictureStatus));
        }
    }

    public string ProfilePictureStatus => string.IsNullOrWhiteSpace(_profilePicturePath)
        ? "No profile picture selected"
        : _profilePicturePath;

    public void SetProfilePicture(string? path)
    {
        if (string.Equals(_profilePicturePath, path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _profilePicturePath = path;
        RefreshAccount();
    }

    private void RefreshAccount() => Account = CreateAccount();

    private AccountIconInfo CreateAccount() => new(
        SelectedProvider.Provider,
        _profilePicturePath,
        AccountColorHex);

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record AccountIconProviderOption(AccountIconProvider Provider, string DisplayName);
