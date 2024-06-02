namespace Wino.Mail.ConsoleTest
{
    public interface ISynchronizerTest
    {
        Task InitializeTestAsync();
        Task StartAsync(CancellationToken token = default);
    }
}
