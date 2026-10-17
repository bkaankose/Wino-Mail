#nullable enable
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using System.Text.Json;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Messaging.UI;
using Wino.Platform.Apple.PushRelay;

namespace Wino.Platform.iOS.PushRelay;

/// <summary>
/// Keeps the Keychain directory the Notification Service Extension reads in step with the accounts: every account's
/// provider, address and token key, keyed by the local account id the relay sends as <c>cid</c>.
/// </summary>
public sealed class RelayExtensionAccountDirectory(IAccountService accounts, ISecureItemStore secureItems, IMessenger messenger)
    : IRecipient<AccountCreatedMessage>, IRecipient<AccountUpdatedMessage>, IRecipient<AccountRemovedMessage>
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger _logger = Log.ForContext<RelayExtensionAccountDirectory>();
    private bool _started;

    public async Task StartAsync()
    {
        if (_started) return;
        _started = true;
        messenger.Register<AccountCreatedMessage>(this);
        messenger.Register<AccountUpdatedMessage>(this);
        messenger.Register<AccountRemovedMessage>(this);
        LogExtensionTrace();
        await RefreshAsync().ConfigureAwait(false);
    }

    /// <summary>The extension's rolling trace from the last pushes (shared Keychain), so device logs show what it did.</summary>
    private void LogExtensionTrace()
    {
        try
        {
            var bytes = secureItems.Read("relay-extension-log", "trace");
            if (bytes is null) { _logger.Information("Notification extension trace: none."); return; }
            _logger.Information("Notification extension trace:\n{Trace}", System.Text.Encoding.UTF8.GetString(bytes));
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "The notification extension trace could not be read.");
        }
    }

    public void Receive(AccountCreatedMessage message) => _ = RefreshAsync();
    public void Receive(AccountUpdatedMessage message) => _ = RefreshAsync();
    public void Receive(AccountRemovedMessage message) => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var list = await accounts.GetAccountsAsync().ConfigureAwait(false) ?? [];
            secureItems.DeleteScope(RelayExtensionShared.DirectoryScope);
            foreach (var account in list)
            {
                var provider = account.ProviderType switch
                {
                    MailProviderType.Outlook => RelayExtensionShared.ProviderGraph,
                    MailProviderType.Gmail => RelayExtensionShared.ProviderGmail,
                    MailProviderType.IMAP4 => RelayExtensionShared.ProviderImap,
                    _ => null
                };
                if (provider is null) continue;

                var entry = new RelayExtensionAccount
                {
                    Provider = provider,
                    Address = account.Address ?? string.Empty,
                    AuthenticationAddress = account.AuthenticationAddress,
                    CredentialKey = account.ProviderType == MailProviderType.Gmail ? account.Id.ToString("N") : null
                };
                secureItems.Write(RelayExtensionShared.DirectoryScope, account.Id.ToString("D"),
                    JsonSerializer.SerializeToUtf8Bytes(entry, RelayExtensionJsonContext.Default.RelayExtensionAccount),
                    SecureItemAccess.ThisDeviceOnlyAfterFirstUnlock);
            }
            _logger.Information("Notification extension directory written for {Count} accounts.", list.Count);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "The notification extension's account directory could not be written.");
        }
        finally
        {
            _lock.Release();
        }
    }
}
