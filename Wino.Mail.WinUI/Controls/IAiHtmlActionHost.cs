using System.Threading;
using System.Threading.Tasks;

namespace Wino.Mail.WinUI.Controls;

public interface IAiHtmlActionHost
{
    Task<string?> GetCurrentHtmlAsync(CancellationToken cancellationToken);
    Task ApplyHtmlResultAsync(string html, CancellationToken cancellationToken);
    Task<string?> TryGetCachedTranslationHtmlAsync(string languageCode, CancellationToken cancellationToken);
    Task SaveCachedTranslationHtmlAsync(string languageCode, string html, CancellationToken cancellationToken);
}
