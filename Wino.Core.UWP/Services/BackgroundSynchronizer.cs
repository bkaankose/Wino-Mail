using System;
using System.Threading.Tasks;
using Serilog;
using Windows.Storage;
using Wino.Core;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Synchronizers;

namespace Wino.Services
{
    public interface IBackgroundSynchronizer
    {
        Task RunBackgroundSynchronizationAsync(BackgroundSynchronizationReason reason);
        void CreateLock();
        void ReleaseLock();
        bool IsBackgroundSynchronizationLocked();
    }

    /// <summary>
    /// Service responsible for handling background synchronization on timer and session connected events.
    /// </summary>
    public class BackgroundSynchronizer : IBackgroundSynchronizer
    {
        private const string BackgroundSynchronizationLock = nameof(BackgroundSynchronizationLock);

        private readonly IAccountService _accountService;
        private readonly IFolderService _folderService;
        private readonly IWinoSynchronizerFactory _winoSynchronizerFactory;

        public BackgroundSynchronizer(IAccountService accountService,
                                     IFolderService folderService,
                                     IWinoSynchronizerFactory winoSynchronizerFactory)
        {
            _accountService = accountService;
            _folderService = folderService;
            _winoSynchronizerFactory = winoSynchronizerFactory;
        }

        public void CreateLock() => ApplicationData.Current.LocalSettings.Values[BackgroundSynchronizationLock] = true;
        public void ReleaseLock() => ApplicationData.Current.LocalSettings.Values[BackgroundSynchronizationLock] = false;

        public bool IsBackgroundSynchronizationLocked()
            => ApplicationData.Current.LocalSettings.Values.ContainsKey(BackgroundSynchronizationLock)
            && ApplicationData.Current.LocalSettings.Values[BackgroundSynchronizationLock] is bool boolValue && boolValue;

        public async Task RunBackgroundSynchronizationAsync(BackgroundSynchronizationReason reason)
        {
            Log.Information($"{reason} background synchronization is kicked in.");

            // This should never crash.
            // We might be in-process or out-of-process.

            //if (IsBackgroundSynchronizationLocked())
            //{
            //    Log.Warning("Background synchronization is locked. Hence another background synchronization is canceled.");
            //    return;
            //}

            try
            {
                CreateLock();

                var accounts = await _accountService.GetAccountsAsync();

                foreach (var account in accounts)
                {
                    // We can't sync broken account.
                    if (account.AttentionReason != AccountAttentionReason.None)
                        continue;

                    // TODO
                    // We can't synchronize without system folder setup is done.
                    //var isSystemFolderSetupDone = await _folderService.CheckSystemFolderSetupDoneAsync(account.Id);

                    //// No need to throw here. It's a background process.
                    //if (!isSystemFolderSetupDone)
                    //    continue;

                    var synchronizer = _winoSynchronizerFactory.GetAccountSynchronizer(account.Id);

                    if (synchronizer.State != AccountSynchronizerState.Idle)
                    {
                        Log.Information("Skipping background synchronization for {Name} since current state is {State}", synchronizer.Account.Name, synchronizer.State);

                        return;
                    }

                    await HandleSynchronizationAsync(synchronizer, reason);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[BackgroundSynchronization] Failed with message {ex.Message}");
            }
            finally
            {
                ReleaseLock();
            }
        }

        private async Task HandleSynchronizationAsync(IBaseSynchronizer synchronizer, BackgroundSynchronizationReason reason)
        {
            if (synchronizer.State != AccountSynchronizerState.Idle) return;

            var account = synchronizer.Account;

            try
            {
                // SessionConnected will do Full synchronization for logon, Timer task will do Inbox only.

                var syncType = reason == BackgroundSynchronizationReason.SessionConnected ? SynchronizationType.Full : SynchronizationType.Inbox;

                var options = new SynchronizationOptions()
                {
                    AccountId = account.Id,
                    Type = syncType,
                };

                await synchronizer.SynchronizeAsync(options);
            }
            catch (AuthenticationAttentionException authenticationAttentionException)
            {
                Log.Error(authenticationAttentionException, $"[BackgroundSync] Invalid credentials for account {account.Address}");

                account.AttentionReason = AccountAttentionReason.InvalidCredentials;
                await _accountService.UpdateAccountAsync(account);
            }
            catch (SystemFolderConfigurationMissingException configMissingException)
            {
                Log.Error(configMissingException, $"[BackgroundSync] Missing system folder configuration for account {account.Address}");

                account.AttentionReason = AccountAttentionReason.MissingSystemFolderConfiguration;
                await _accountService.UpdateAccountAsync(account);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[BackgroundSync] Synchronization failed.");
            }
        }
    }
}
