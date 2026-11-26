using System.Globalization;
using AppKit;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Reader;
using Wino.Editor;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.MacOS.Rendering;
using Wino.Mail.Rendering;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Renderer;
using Wino.Renderer.Accessibility;
using Wino.Renderer.Policy;
using Wino.Renderer.Print;
using Wino.Renderer.Theme;
using Wino.Services.Models.Reader;

namespace Wino.Mail.MacOS.Views.Mail;

/// <summary>
/// The body through the native mail renderer (Windows MailRenderingPage): the view model hands the parsed message to
/// <see cref="IMailMessageReader.RenderAsync"/>, the shared <see cref="EngineMailMessageReader"/> shows it, and the
/// page keeps the presentation (theme, typography), links, the context menu and printing.
/// </summary>
public sealed partial class MailRenderingPageViewController : IMailMessageReader
{
    private MailEngineHost EngineHost => services.GetRequiredService<MailEngineHost>();

    private void InitializeEngineReader()
    {
        if (_reader is not null) return;
        _reader = new EngineMailMessageReader(_mailView, EngineHost, _presentation, ViewModel.SecureMimeContextFactory,
            () => ViewModel.ShouldStripLinkTrackingParameters);
        _mailView.LinkActivated += MailView_LinkActivated;
        _mailView.LinkHoverChanged += MailView_LinkHoverChanged;
        _mailView.ContextMenuRequested += MailView_ContextMenuRequested;
        // The engine needs the primary press and release itself: a click recognizer that delays them swallows a click on a reply row.
        _mailView.AddGestureRecognizer(new NSClickGestureRecognizer(MailView_Clicked) { DelaysPrimaryMouseButtonEvents = false });
        _reader.FoldRowTemplate = AppKitFoldRowTemplate.Instance;
        _reader.InsightsChanged += Reader_InsightsChanged;
        _reader.Attach();
    }

    private void Reader_InsightsChanged(object? sender, MailReaderInsights insights) => ViewModel.ShowInsights(insights);

    private void ApplyReaderPresentation()
    {
        _presentation.IsDark = _dark;
        _presentation.Method = ViewModel.ReaderDarkModeMethod == MailDarkModeMethod.Inverted ? DarkModeMethod.Inverted : DarkModeMethod.Adaptive;
        _presentation.IsHighContrast = NSWorkspace.SharedWorkspace.AccessibilityDisplayShouldIncreaseContrast;
        _presentation.SetTypography(ViewModel.PreferencesService.ReaderFont, ViewModel.PreferencesService.ReaderFontSize);
        _presentation.ApplyTo(_mailView);
    }

    async Task<MailRenderModel> IMailMessageReader.RenderAsync(MailMessageRenderRequest request, CancellationToken cancellationToken)
    {
        if (_reader is null || _disposedReader) throw new OperationCanceledException();
        ApplyReaderPresentation();
        _linkTarget.Clear();
        UpdateAccessibleMailContext(string.Empty);

        // Read before the load starts: the engine's worker owns the message until the load ends.
        _currentRenderedHtml = EngineMailMessageReader.GetBodyHtml(request.Message);
        var intelligence = IntelligenceHtmlRendered(_currentRenderedHtml);
        _reader.IsReaderView = ViewModel.IsReaderViewEnabled;
        var model = await _reader.RenderAsync(request, cancellationToken);
        UpdateAccessibleMailContext(model.AccessibleText);
        await intelligence;

        // An encrypted message has no readable body until the engine decrypted it.
        if (_currentRenderedHtml.Length == 0 && _reader.GetLoadedBodyHtml() is { Length: > 0 } resolved)
        {
            _currentRenderedHtml = resolved;
            await IntelligenceHtmlRendered(_currentRenderedHtml);
        }

        return model;
    }

    Task IMailMessageReader.ClearAsync()
    {
        _currentRenderedHtml = string.Empty;
        _linkTarget.Clear();
        IntelligenceContentCleared();
        return _reader is null || _disposedReader ? Task.CompletedTask : _reader.ClearAsync();
    }

    /// <summary>Shows the translation while one is applied, else the message itself.</summary>
    private async Task RenderActiveContentAsync()
    {
        if (_reader is null || _disposedReader) return;
        var html = await ResolveActiveHtmlAsync(_currentRenderedHtml);
        if (ReferenceEquals(html, _currentRenderedHtml) || html == _currentRenderedHtml)
            await _reader.ShowOriginalAsync();
        else
            await _reader.ShowHtmlVariantAsync(html);
    }

    private void UpdateAccessibleMailContext(string accessibleText)
    {
        var subject = string.IsNullOrWhiteSpace(ViewModel.Subject) ? Translator.MailItemNoSubject : ViewModel.Subject;
        _mailView.AccessibilityContext = new AccessibilityContext
        {
            Subject = subject,
            Sender = FormatSender(),
            Date = MailRowMapper.FormatReaderDate(ViewModel.CreationDate, ViewModel.PreferencesService.MailTimeFormatPreference),
            BodyName = Translator.Reader_MessageBodyAutomationName,
            PlainTextFallbackName = Translator.Reader_PlainTextFallbackAutomationName,
            TrimmedContentName = Translator.Reader_EarlierMessagesButtonName,
            FallbackText = accessibleText
        };
    }

    private string FormatSender()
        => string.IsNullOrWhiteSpace(ViewModel.FromName)
            ? ViewModel.FromAddress
            : string.IsNullOrWhiteSpace(ViewModel.FromAddress) ? ViewModel.FromName : $"{ViewModel.FromName} <{ViewModel.FromAddress}>";

    private async void MailView_LinkActivated(object? sender, LinkEventArgs e)
    {
        try
        {
            if (CreateResolvedLink(e.Link, e.Text) is { } link)
                (await ViewModel.OpenLinkAsync(link)).ThrowIfNotSucceeded();
        }
        catch (Exception exception)
        {
            ReportError(exception);
        }
    }

    private static ResolvedMailLink? CreateResolvedLink(ResolvedLink link, string? text)
    {
        switch (link.Kind)
        {
            case LinkKind.Web:
                if (!Uri.TryCreate(link.Effective, UriKind.Absolute, out var address)) return null;
                var original = !string.Equals(link.Original, link.Effective, StringComparison.Ordinal)
                    && Uri.TryCreate(link.Original, UriKind.Absolute, out var originalAddress) ? originalAddress : null;
                return new ResolvedMailLink(address, text, link.RemovedTrackingParameters, original);
            case LinkKind.Mailto:
            case LinkKind.Tel:
                return Uri.TryCreate(link.Original, UriKind.Absolute, out var protocolAddress) ? new ResolvedMailLink(protocolAddress, text) : null;
            default:
                return null;
        }
    }

    private void MailView_LinkHoverChanged(object? sender, LinkHoverEventArgs e)
    {
        if (e.Link is not { Kind: not LinkKind.Blocked } link)
        {
            _linkTarget.Clear();
            return;
        }

        var safeTarget = link.Protection == LinkProtection.OutlookSafeLinks && link.RedirectTarget is { Length: > 0 } target ? target : null;
        var mismatch = e.TextMismatch is { } textMismatch
            ? string.Format(CultureInfo.CurrentCulture, Translator.Reader_LinkTextMismatchFormat, textMismatch.TextHost, textMismatch.TargetHost)
            : string.Empty;
        _linkTarget.Show(new RendererLinkHoverEventArgs(link.Kind == LinkKind.Web ? link.Effective : link.Original, safeTarget), mismatch);
    }

    private IMailPrintPresenter CreatePrintPresenter()
        => new MacEngineMailPrintPresenter(EngineHost, () => _disposedReader ? null : _mailView, () => _reader?.HasMessage == true ? _mailView.Reader : null,
            CreatePrintHeader, Dispatcher);

    private PrintHeader CreatePrintHeader()
        => new(string.IsNullOrWhiteSpace(ViewModel.Subject) ? Translator.MailItemNoSubject : ViewModel.Subject, FormatSender(),
            FormatPrintRecipients(ViewModel.ToItems), FormatPrintRecipients(ViewModel.CcItems),
            MailRowMapper.FormatReaderDate(ViewModel.CreationDate, ViewModel.PreferencesService.MailTimeFormatPreference));

    private static string FormatPrintRecipients(IEnumerable<AccountContactViewModel> recipients)
        => string.Join("; ", recipients.Select(recipient =>
            string.IsNullOrWhiteSpace(recipient.Name) || string.Equals(recipient.Name, recipient.Address, StringComparison.OrdinalIgnoreCase)
                ? recipient.Address
                : $"{recipient.Name} <{recipient.Address}>"));
}
