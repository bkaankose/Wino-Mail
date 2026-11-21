#if DEBUG
namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

internal static partial class MailFiltersDebugActions
{
    private static partial Task<bool> RunEditorAsync(IServiceProvider provider, string[] parts) => Task.FromResult(false);
}
#endif
