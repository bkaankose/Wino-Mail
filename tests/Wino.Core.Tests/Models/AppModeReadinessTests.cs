using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.ViewModels.Data;
using Xunit;

namespace Wino.Core.Tests.Models;

public class AppModeReadinessTests
{
    [Fact]
    public void Evaluate_NoAccounts_IsNoAccounts()
    {
        var readiness = AppModeReadiness.Evaluate(WinoApplicationMode.Tasks, [], _ => true);

        readiness.State.Should().Be(AppModeReadinessState.NoAccounts);
        readiness.IsReady.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_ModeOffOnEveryAccount_IsFeatureDisabled()
    {
        var account = new MailAccount { Id = Guid.NewGuid(), IsTaskAccessEnabled = false };

        var readiness = AppModeReadiness.Evaluate(WinoApplicationMode.Tasks, [account], _ => true);

        readiness.State.Should().Be(AppModeReadinessState.FeatureDisabled);
    }

    [Fact]
    public void Evaluate_CalendarEnabledWithoutProviderAccess_CountsAsOn()
    {
        var account = new MailAccount { Id = Guid.NewGuid(), IsCalendarAccessEnabled = true, IsCalendarAccessGranted = false };

        var readiness = AppModeReadiness.Evaluate(WinoApplicationMode.Calendar, [account], _ => true);

        readiness.IsReady.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_RestoredAccountWithoutData_AsksForSignIn()
    {
        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            IsContactAccessEnabled = true,
            AttentionReason = AccountAttentionReason.InvalidCredentials
        };

        var readiness = AppModeReadiness.Evaluate(WinoApplicationMode.Contacts, [account], _ => false);

        readiness.State.Should().Be(AppModeReadinessState.SignInRequired);
        readiness.AttentionAccount.Should().BeSameAs(account);
    }

    [Fact]
    public void Evaluate_SignedInAccountWithoutData_WaitsForSynchronization()
    {
        var account = new MailAccount { Id = Guid.NewGuid(), IsCalendarAccessGranted = true };

        var readiness = AppModeReadiness.Evaluate(WinoApplicationMode.Calendar, [account], _ => false);

        readiness.State.Should().Be(AppModeReadinessState.WaitingForSynchronization);
    }

    [Fact]
    public void Evaluate_AnyAccountWithData_IsReady_EvenWhenAnotherNeedsSignIn()
    {
        var ready = new MailAccount { Id = Guid.NewGuid(), IsTaskAccessEnabled = true };
        var restored = new MailAccount
        {
            Id = Guid.NewGuid(),
            IsTaskAccessEnabled = true,
            AttentionReason = AccountAttentionReason.InvalidCredentials
        };

        var readiness = AppModeReadiness.Evaluate(WinoApplicationMode.Tasks, [restored, ready], account => account.Id == ready.Id);

        readiness.IsReady.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_AccountWithModeOff_DoesNotMakeTheModeReady()
    {
        var disabled = new MailAccount { Id = Guid.NewGuid(), IsTaskAccessEnabled = false };
        var enabled = new MailAccount { Id = Guid.NewGuid(), IsTaskAccessEnabled = true };

        var readiness = AppModeReadiness.Evaluate(WinoApplicationMode.Tasks, [disabled, enabled], account => account.Id == disabled.Id);

        readiness.State.Should().Be(AppModeReadinessState.WaitingForSynchronization);
    }

    [Fact]
    public void Evaluate_Settings_IsAlwaysReady()
        => AppModeReadiness.Evaluate(WinoApplicationMode.Settings, [], _ => false).IsReady.Should().BeTrue();

    [Fact]
    public async Task ModeReadinessViewModel_BlocksAndReleases_OnInvalidation()
    {
        var service = new Mock<IAppModeReadinessService>();
        service.SetupSequence(s => s.GetReadinessAsync(WinoApplicationMode.Tasks, default))
            .ReturnsAsync(new AppModeReadiness(WinoApplicationMode.Tasks, AppModeReadinessState.NoAccounts))
            .ReturnsAsync(AppModeReadiness.Ready(WinoApplicationMode.Tasks));

        var viewModel = new ModeReadinessViewModel(WinoApplicationMode.Tasks, service.Object, null, null, null);
        var changes = 0;
        viewModel.ReadinessChanged += (_, _) => changes++;

        await viewModel.ActivateAsync();

        viewModel.IsBlocked.Should().BeTrue();
        viewModel.IsPrimaryActionVisible.Should().BeTrue();
        viewModel.PrimaryActionCommand.CanExecute(null).Should().BeTrue();

        (await viewModel.EnsureReadyAsync()).Should().BeTrue();

        viewModel.IsReady.Should().BeTrue();
        viewModel.PrimaryActionCommand.CanExecute(null).Should().BeFalse();
        changes.Should().Be(2);
    }

    [Fact]
    public async Task ModeReadinessViewModel_SignIn_ForReconsentOnlyAccount_UsesFixAccount()
    {
        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            ProviderType = MailProviderType.Outlook,
            AttentionReason = AccountAttentionReason.None,
            IsContactAccessEnabled = true,
            IsContactAccessGranted = true,
            IsContactReauthorizationRequired = true,
            ContactIntegrationSource = AccountIntegrationSource.Provider
        };
        var service = new Mock<IAppModeReadinessService>();
        service.Setup(s => s.GetReadinessAsync(WinoApplicationMode.Contacts, default))
            .ReturnsAsync(new AppModeReadiness(WinoApplicationMode.Contacts, AppModeReadinessState.SignInRequired, account));
        var shell = new Mock<IMailShellClient>();
        shell.Setup(s => s.HandleAccountAttentionAsync(account)).Returns(Task.CompletedTask);
        var navigation = new Mock<INavigationService>();
        var viewModel = new ModeReadinessViewModel(WinoApplicationMode.Contacts, service.Object, navigation.Object, shell.Object, null);

        await viewModel.ActivateAsync();
        await viewModel.PrimaryActionCommand.ExecuteAsync(null);

        shell.Verify(s => s.HandleAccountAttentionAsync(account), Times.Once);
        navigation.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task ModeReadinessViewModel_SignIn_ForImapReconsentFlag_OpensManageAccounts()
    {
        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            ProviderType = MailProviderType.IMAP4,
            AttentionReason = AccountAttentionReason.None,
            IsTaskAccessEnabled = true,
            IsTaskReauthorizationRequired = true
        };
        var service = new Mock<IAppModeReadinessService>();
        service.Setup(s => s.GetReadinessAsync(WinoApplicationMode.Tasks, default))
            .ReturnsAsync(new AppModeReadiness(WinoApplicationMode.Tasks, AppModeReadinessState.SignInRequired, account));
        var shell = new Mock<IMailShellClient>();
        var navigation = new Mock<INavigationService>();
        var viewModel = new ModeReadinessViewModel(WinoApplicationMode.Tasks, service.Object, navigation.Object, shell.Object, null);

        await viewModel.ActivateAsync();
        await viewModel.PrimaryActionCommand.ExecuteAsync(null);

        shell.Verify(s => s.HandleAccountAttentionAsync(It.IsAny<MailAccount>()), Times.Never);
        navigation.Invocations.Should().NotBeEmpty();
    }

    [Fact]
    public void NeedsSignIn_LocalModesWithStaleConsentFlags_IsFalse()
    {
        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            ProviderType = MailProviderType.Gmail,
            IsContactAccessEnabled = true,
            IsContactReauthorizationRequired = true,
            ContactIntegrationSource = AccountIntegrationSource.Local,
            IsTaskAccessEnabled = true,
            IsTaskReauthorizationRequired = true,
            TaskIntegrationSource = AccountIntegrationSource.Local
        };

        AppModeReadiness.NeedsSignIn(account, WinoApplicationMode.Contacts).Should().BeFalse();
        AppModeReadiness.NeedsSignIn(account, WinoApplicationMode.Tasks).Should().BeFalse();
        AppModeReadiness.Evaluate(WinoApplicationMode.Contacts, [account], _ => true)
            .State.Should().NotBe(AppModeReadinessState.SignInRequired);
    }

    [Fact]
    public async Task ModeReadinessViewModel_WithoutService_IsAlwaysReady()
    {
        var viewModel = new ModeReadinessViewModel(WinoApplicationMode.Calendar, null, null, null, null);

        await viewModel.ActivateAsync();

        viewModel.IsReady.Should().BeTrue();
        (await viewModel.EnsureReadyAsync()).Should().BeTrue();
    }
}
