using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Intelligence.ConsoleApp.Hosting;

namespace Wino.Intelligence.ConsoleApp.Scenarios;

/// <summary>What every scenario runs against: the service graph and the chosen mail account.</summary>
internal sealed class ScenarioContext(IServiceProvider services, ApiTarget apiTarget, SynchronizationHost synchronization)
{
    public IServiceProvider Services { get; } = services;
    public ApiTarget ApiTarget { get; } = apiTarget;
    public SynchronizationHost Synchronization { get; } = synchronization;
    public MailAccount Account { get; private set; } = null!;

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    public void Select(MailAccount account) => Account = account;

    /// <summary>Re-reads the account so preference changes made by services are visible.</summary>
    public async Task<MailAccount> ReloadAccountAsync()
    {
        Account = await Get<IAccountService>().GetAccountAsync(Account.Id).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The selected account no longer exists.");
        return Account;
    }
}

/// <summary>One entry of the account menu.</summary>
internal sealed record Scenario(string Key, string Title, string Description, Func<ScenarioContext, CancellationToken, Task> RunAsync);
