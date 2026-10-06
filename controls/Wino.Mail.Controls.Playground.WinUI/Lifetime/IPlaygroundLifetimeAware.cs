namespace Wino.Mail.Controls.Playground.Lifetime;

internal interface IPlaygroundLifetimeAware
{
    IEnumerable<object> AdditionalLifetimeObjects => Array.Empty<object>();

    Task PrepareForLifetimeTestAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
