using Microsoft.Extensions.DependencyInjection;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Reader;
using Wino.Editor;
using Wino.Mail.ViewModels.Collections;
using Wino.Mail.Rendering;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Renderer;
using Wino.Renderer.Accessibility;
using Wino.Renderer.Policy;
using Wino.Renderer.Primitives;
using Wino.Renderer.Print;
using Wino.Renderer.Theme;
using Wino.Presentation.UIKit;
using Wino.Services.Models.Reader;

namespace Wino.Mail.UIKit.Views.Mail;

/// <summary>
/// The body through the native mail renderer (Windows MailRenderingPage, Mac MailRenderingPageViewController.Engine):
/// the view model hands the parsed message to <see cref="IMailMessageReader.RenderAsync"/>, the shared
/// <see cref="EngineMailMessageReader"/> shows it, and the page keeps the presentation (theme, typography, the phone
/// layout of a mail), links, the long-press menu and printing.
/// </summary>
public sealed partial class MailRenderingPageViewController : IMailMessageReader
{
    private MailEngineHost EngineHost => _services.GetRequiredService<MailEngineHost>();

    private void InitializeEngineReader()
    {
        if (_reader is not null) return;
        _reader = new EngineMailMessageReader(_mailView, EngineHost, _presentation, ViewModel.SecureMimeContextFactory,
            () => ViewModel.ShouldStripLinkTrackingParameters);
        _mailView.LinkActivated += MailView_LinkActivated;
        _mailView.LinkHoverChanged += MailView_LinkHoverChanged;
        _mailView.ContextMenuRequested += MailView_ContextMenuRequested;
        WinoTraits.OnAppearanceChanged(_mailView, _ => AppearanceChanged());
        _reader.Attach();
    }

    private void ApplyReaderPresentation()
    {
        _presentation.IsDark = _dark;
        _presentation.Method = ViewModel.ReaderDarkModeMethod == MailDarkModeMethod.Inverted ? DarkModeMethod.Inverted : DarkModeMethod.Adaptive;
        _presentation.IsHighContrast = UIAccessibility.DarkerSystemColorsEnabled;
        _presentation.SetTypography(ViewModel.PreferencesService.ReaderFont, ViewModel.PreferencesService.ReaderFontSize);
        // A mail's phone layout applies on the device it is read on, and a wide mail is fitted to the card.
        var screen = UIScreen.MainScreen.Bounds.Size;
        _presentation.Viewport = _presentation.Viewport with
        {
            ContentInset = BodyInset,
            FitToWidth = true,
            ScreenSize = new PxSize((float)Math.Min(screen.Width, screen.Height), (float)Math.Max(screen.Width, screen.Height))
        };
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
        UpdateBodyHeight();
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
        UpdateBodyHeight();
    }

    private void UpdateAccessibleMailContext(string accessibleText)
    {
        var subject = string.IsNullOrWhiteSpace(ViewModel.Subject) ? Translator.MailItemNoSubject : ViewModel.Subject;
        _mailView.AccessibilityContext = new AccessibilityContext
        {
            Subject = subject,
            Sender = FormatSender(),
            Date = MailRowPresentation.FormatReaderDate(ViewModel.CreationDate, ViewModel.PreferencesService.MailTimeFormatPreference),
            BodyName = Translator.Reader_MessageBodyAutomationName,
            PlainTextFallbackName = Translator.Reader_PlainTextFallbackAutomationName,
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

    // The pointer on iPad hovers links; touch never does.
    private void MailView_LinkHoverChanged(object? sender, LinkHoverEventArgs e)
    {
        if (e.Link is not { Kind: not LinkKind.Blocked } link)
        {
            _linkTarget.Clear();
            return;
        }

        var safeTarget = link.Protection == LinkProtection.OutlookSafeLinks && link.RedirectTarget is { Length: > 0 } target ? target : null;
        _linkTarget.Show(new RendererLinkHoverEventArgs(link.Kind == LinkKind.Web ? link.Effective : link.Original, safeTarget));
    }

    // A long press on a link or an image: an action sheet anchored at the finger.
    private void MailView_ContextMenuRequested(object? sender, ContextMenuRequest request)
    {
        var sheet = UIAlertController.Create(request.LinkUrl, null, UIAlertControllerStyle.ActionSheet);
        if (!string.IsNullOrEmpty(request.LinkUrl))
        {
            var url = request.LinkUrl;
            if (Uri.TryCreate(url, UriKind.Absolute, out var address))
                sheet.AddAction(UIAlertAction.Create(Translator.Buttons_Open, UIAlertActionStyle.Default,
                    _ => Observe(OpenAsync(new ResolvedMailLink(address, null)))));
            sheet.AddAction(UIAlertAction.Create(Translator.Reader_ContextMenu_CopyLink, UIAlertActionStyle.Default,
                _ => Observe(ViewModel.CopyClipboardCommand.ExecuteAsync(url))));
        }

        if (request.HasSelection)
            sheet.AddAction(UIAlertAction.Create(Translator.Buttons_Copy, UIAlertActionStyle.Default, _ => _mailView.CopySelection()));

        sheet.AddAction(UIAlertAction.Create(Translator.Reader_ContextMenu_SelectAll, UIAlertActionStyle.Default, _ => _mailView.SelectAll()));
        sheet.AddAction(UIAlertAction.Create(Translator.Buttons_Cancel, UIAlertActionStyle.Cancel, null));
        if (sheet.PopoverPresentationController is { } popover)
        {
            popover.SourceView = _mailView;
            var point = _mailView.ConvertPointFromView(new CoreGraphics.CGPoint(request.ViewPoint.X, request.ViewPoint.Y), _mailView.ScrollView);
            popover.SourceRect = new CoreGraphics.CGRect(point.X + _mailView.ScrollView.ContentOffset.X, point.Y + _mailView.ScrollView.ContentOffset.Y, 1, 1);
        }

        PresentViewController(sheet, true, null);
    }

    private async Task OpenAsync(ResolvedMailLink link) => (await ViewModel.OpenLinkAsync(link)).ThrowIfNotSucceeded();

    private IMailPrintPresenter CreatePrintPresenter()
        => new UIKitEngineMailPrintPresenter(EngineHost, () => _disposedReader ? null : _mailView, () => _reader?.HasMessage == true ? _mailView.Reader : null,
            CreatePrintHeader, Dispatcher);

    private PrintHeader CreatePrintHeader()
        => new(string.IsNullOrWhiteSpace(ViewModel.Subject) ? Translator.MailItemNoSubject : ViewModel.Subject, FormatSender(),
            FormatPrintRecipients(ViewModel.ToItems), FormatPrintRecipients(ViewModel.CcItems),
            MailRowPresentation.FormatReaderDate(ViewModel.CreationDate, ViewModel.PreferencesService.MailTimeFormatPreference));

    private static string FormatPrintRecipients(IEnumerable<AccountContactViewModel> recipients)
        => string.Join("; ", recipients.Select(recipient =>
            string.IsNullOrWhiteSpace(recipient.Name) || string.Equals(recipient.Name, recipient.Address, StringComparison.OrdinalIgnoreCase)
                ? recipient.Address
                : $"{recipient.Name} <{recipient.Address}>"));
}
