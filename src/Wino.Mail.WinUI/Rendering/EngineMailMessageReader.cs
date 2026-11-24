using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MimeKit;
using MimeKit.Cryptography;
using MimeKit.Text;
using Serilog;
using Wino.Core.Domain.Models.Reader;
using Wino.Mail.ViewModels;
using Wino.Renderer;
using Wino.Renderer.Mime;
using Wino.Renderer.Policy;
using Wino.Renderer.Primitives;
using Wino.Renderer.WinUI;
using Wino.Services.Extensions;
using Wino.Services.Models.Reader;
using DomainBlockedResourceReason = Wino.Core.Domain.Enums.BlockedResourceReason;
using DomainRemoteContentMode = Wino.Core.Domain.Enums.RemoteContentMode;

namespace Wino.Mail.WinUI.Rendering;

/// <summary>
/// Shows a parsed message in a <see cref="MailView"/> and reports what the reader page needs from it.
/// </summary>
public sealed class EngineMailMessageReader : IMailMessageReader
{
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromMilliseconds(750);
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly MailView _view;
    private readonly MailEngineHost _engineHost;
    private readonly EngineReaderPresentation _presentation;
    private readonly Func<SecureMimeContext> _secureMimeContextFactory;
    private readonly Func<bool> _shouldStripTrackingParameters;

    private MimeMailContent? _content;
    private MimeMessage? _message;
    private ReaderLoadOptions? _loadOptions;
    private string _accessibleText = string.Empty;
    private bool _isVariantShown;
    private bool _isReaderView;
    private int _loadVersion;

    public EngineMailMessageReader(MailView view,
                                   MailEngineHost engineHost,
                                   EngineReaderPresentation presentation,
                                   Func<SecureMimeContext> secureMimeContextFactory,
                                   Func<bool> shouldStripTrackingParameters)
    {
        _view = view;
        _engineHost = engineHost;
        _presentation = presentation;
        _secureMimeContextFactory = secureMimeContextFactory;
        _shouldStripTrackingParameters = shouldStripTrackingParameters;
    }

    /// <summary>True while a message is loaded, also when a translated variant of it is on screen.</summary>
    public bool HasMessage => _content is not null;

    public bool IsReaderView
    {
        get => _isReaderView;
        set
        {
            _isReaderView = value;

            if (_view.Reader is { } reader)
                reader.Mode = ToRenderMode(value);
        }
    }

    /// <summary>Attaches the view to the process engine. Safe to call again after <see cref="Detach"/>.</summary>
    public void Attach()
    {
        if (_view.Engine is null)
            _view.Engine = _engineHost.Engine;
    }

    /// <summary>Releases the session and the message. The view shows nothing until <see cref="Attach"/>.</summary>
    public void Detach()
    {
        Interlocked.Increment(ref _loadVersion);
        _view.Engine = null;
        ReleaseContent();
    }

    /// <summary>
    /// The message body as HTML for features that work on an HTML string. Reads the message's own HTML part, or its
    /// text part converted to HTML. Empty when the body is not readable without decryption.
    /// </summary>
    public static string GetBodyHtml(MimeMessage message)
    {
        if (message.HtmlBody is { Length: > 0 } html)
            return html;

        return message.TextBody is { Length: > 0 } text
            ? new TextToHtml().Convert(text)
            : string.Empty;
    }

    /// <summary>
    /// The body the engine shows for the loaded message, as HTML: the same string as <see cref="GetBodyHtml"/> for a
    /// message that is readable as it is, and the decrypted body for an encrypted one. Empty when nothing is loaded.
    /// It reads the bytes the engine already decoded, never the message's parts, which the engine may still be reading.
    /// </summary>
    public string GetLoadedBodyHtml()
    {
        if (_content?.Body is not { Kind: not MailBodyKind.None, Bytes.IsEmpty: false } body)
            return string.Empty;

        var text = DecodeBody(body).TrimStart('﻿');

        return body.Kind == MailBodyKind.Html ? text : new TextToHtml().Convert(text);
    }

    private static string DecodeBody(MailBody body)
    {
        var bytes = body.Bytes.Span;

        if (body.DeclaredCharset is { Length: > 0 } charset)
        {
            try
            {
                return Encoding.GetEncoding(charset).GetString(bytes);
            }
            catch (ArgumentException ex)
            {
                Log.Debug(ex, "The message body declares a character set that is not available.");
            }
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    public async Task<MailRenderModel> RenderAsync(MailMessageRenderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stopwatch = Stopwatch.StartNew();
        var version = Interlocked.Increment(ref _loadVersion);
        var message = request.Message;
        var options = request.Options;

        Attach();
        _presentation.ApplyTo(_view);

        // A re-render of the open message (another remote content mode) keeps its content and its scroll position.
        var content = ReferenceEquals(_message, message) ? _content : null;
        var accessibleText = _accessibleText;
        Exception? contentError = null;
        MailLoadResult result;

        if (content is not null)
        {
            result = await _view.LoadAsync(content, CreateLoadOptions(options, !_isVariantShown), cancellationToken);
        }
        else
        {
            var contentOptions = new MimeContentOptions { SecureMimeContextFactory = _secureMimeContextFactory };

            // The message is read on the engine's worker thread only. Nothing else may read its parts until the load ends.
            IMailContent CreateContent(CancellationToken workerToken)
            {
                try
                {
                    content = MimeMailContent.Create(message, contentOptions);
                    workerToken.ThrowIfCancellationRequested();
                    accessibleText = GetAccessibleText(message, content);

                    // Worked out here so a message without a List-Unsubscribe header has its body searched off the UI thread.
                    _ = content.Analysis.Unsubscribe;
                    return content;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    contentError = ex;
                    throw;
                }
            }

            result = await _view.LoadAsync(CreateContent, CreateLoadOptions(options, false), cancellationToken);
        }

        var loadMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

        if (version != Volatile.Read(ref _loadVersion))
        {
            if (!ReferenceEquals(content, _content))
                content?.Dispose();

            throw new OperationCanceledException(cancellationToken);
        }

        if (contentError is not null)
            throw new InvalidOperationException(contentError.Message, contentError);

        if (result.IsCancelled || content is null)
        {
            if (!ReferenceEquals(content, _content))
                content?.Dispose();

            cancellationToken.ThrowIfCancellationRequested();
            throw new OperationCanceledException();
        }

        if (!ReferenceEquals(content, _content))
        {
            _content?.Dispose();
            _content = content;
        }

        _message = message;
        _accessibleText = accessibleText;
        _loadOptions = CreateLoadOptions(options, false);
        _isVariantShown = false;

        var firstFrameMilliseconds = await WaitForFirstFrameAsync(stopwatch);

        LogRender(request.MimePath, result, content, loadMilliseconds, firstFrameMilliseconds);

        return CreateRenderModel(options, content.Analysis, result, accessibleText);
    }

    /// <summary>
    /// Shows <paramref name="html"/> in place of the loaded message's body, with the message's own inline resources
    /// and the policy it was loaded with.
    /// </summary>
    public async Task ShowHtmlVariantAsync(string html)
    {
        if (_content is not { } content || _loadOptions is not { } loadOptions)
            return;

        var version = Volatile.Read(ref _loadVersion);
        var variant = new MessageHtmlVariantContent(HtmlMailContent.FromHtml(html), content);

        await _view.LoadAsync(variant, loadOptions with { Mode = ToRenderMode(_isReaderView), Presentation = _presentation.CreateOptions() });

        if (version == Volatile.Read(ref _loadVersion))
            _isVariantShown = true;
    }

    /// <summary>Puts the loaded message back after <see cref="ShowHtmlVariantAsync"/>.</summary>
    public async Task ShowOriginalAsync()
    {
        if (!_isVariantShown || _content is not { } content || _loadOptions is not { } loadOptions)
            return;

        var version = Volatile.Read(ref _loadVersion);

        await _view.LoadAsync(content, loadOptions with { Mode = ToRenderMode(_isReaderView), Presentation = _presentation.CreateOptions() });

        if (version == Volatile.Read(ref _loadVersion))
            _isVariantShown = false;
    }

    public Task ClearAsync()
    {
        Interlocked.Increment(ref _loadVersion);

        if (_view.Engine is not null)
            _view.Clear();

        ReleaseContent();
        return Task.CompletedTask;
    }

    /// <summary>Removes the message and lets the engine release what it decoded for it.</summary>
    public async Task EnterIdleAsync()
    {
        await ClearAsync();
        _engineHost.Trim(MemoryTrimLevel.Idle);
    }

    private void ReleaseContent()
    {
        _content?.Dispose();
        _content = null;
        _message = null;
        _accessibleText = string.Empty;
        _loadOptions = null;
        _isVariantShown = false;
    }

    private ReaderLoadOptions CreateLoadOptions(MailRenderingOptions? options, bool keepScrollPosition)
    {
        var remoteContent = options?.RemoteContent ?? DomainRemoteContentMode.BlockAll;
        var isEverythingBlocked = remoteContent == DomainRemoteContentMode.BlockAll;

        return new ReaderLoadOptions
        {
            User = new UserPolicyAdjustments
            {
                RemoteContent = ToEngineMode(remoteContent),
                AuthorStyleSheets = options?.LoadStyles ?? true,
                Linkify = options?.RenderPlaintextLinks ?? true,
                StripTrackingParameters = _shouldStripTrackingParameters(),
                UnwrapRedirectsOnActivation = true
            },
            Mode = ToRenderMode(_isReaderView),
            KeepScrollPosition = keepScrollPosition,
            Accessibility = _view.AccessibilityContext,

            // Block all covers images only inside the engine; web fonts and font style sheets are their own switches.
            RemoteFonts = !isEverythingBlocked,
            FontStylesheets = !isEverythingBlocked,
            Presentation = _presentation.CreateOptions()
        };
    }

    private string GetAccessibleText(MimeMessage message, MimeMailContent content)
    {
        var textBody = message.TextBody;

        return string.IsNullOrWhiteSpace(textBody)
            ? _engineHost.Engine.Text.GetReadableText(content)
            : textBody.Trim();
    }

    private static MailRenderModel CreateRenderModel(MailRenderingOptions? options, MimeMailAnalysis analysis, MailLoadResult result, string accessibleText)
    {
        var model = new MailRenderModel(string.Empty, options, accessibleText)
        {
            RemoteImageCount = result.RemoteResourceCount,
            IsSmimeEncrypted = analysis.IsEncrypted,
            Signatures = new Dictionary<IDigitalSignature, bool>(analysis.Signatures)
        };

        model.Attachments.AddRange(analysis.Attachments.OfType<MimePart>().Where(MailAttachmentExtensions.IsMailAttachment));

        model.UnsubscribeInfo = EngineUnsubscribeReader.ToDomain(analysis.Unsubscribe);

        foreach (var blocked in result.BlockedResources)
        {
            if (ToDomainReason(blocked) is { } reason)
                model.BlockedResources.Add(new BlockedRemoteResource(blocked.Host, blocked.Url, reason));
        }

        return model;
    }

    private async Task<double> WaitForFirstFrameAsync(Stopwatch stopwatch)
    {
        if (!_view.IsLoaded)
            return -1;

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFrameRendered(object? sender, EventArgs e) => completion.TrySetResult(true);

        _view.FrameRendered += OnFrameRendered;

        try
        {
            var finished = await Task.WhenAny(completion.Task, Task.Delay(FirstFrameTimeout));
            return ReferenceEquals(finished, completion.Task) ? stopwatch.Elapsed.TotalMilliseconds : -1;
        }
        finally
        {
            _view.FrameRendered -= OnFrameRendered;
        }
    }

    private static void LogRender(string? mimePath, MailLoadResult result, MimeMailContent content, double loadMilliseconds, double firstFrameMilliseconds)
    {
        var timings = result.Timings;

        Log.Debug(
            "Mail render {FileId}: load {LoadMs:F1} ms, first frame {FirstFrameMs:F1} ms, kind {BodyKind}, body {BodyBytes} bytes, " +
            "engine decode {DecodeMs:F1} parse {ParseMs:F1} sanitize {SanitizeMs:F1} style {StyleMs:F1} layout {LayoutMs:F1} paint {PaintMs:F1} total {EngineTotalMs:F1} ms, " +
            "remote {RemoteCount}, blocked {BlockedCount}, limits {LimitCount}",
            GetFileId(mimePath),
            loadMilliseconds,
            firstFrameMilliseconds,
            result.BodyKind,
            content.Body.Bytes.Length,
            timings.DecodeMs,
            timings.ParseMs,
            timings.SanitizeMs,
            timings.StyleMs,
            timings.LayoutMs,
            timings.PaintMs,
            timings.TotalToFirstFrameMs,
            result.RemoteResourceCount,
            result.BlockedResources.Count,
            result.LimitsHit.Count);
    }

    // Only a storage identifier may reach the log: the folder of a stored message is named after its file id.
    // Any other path (an EML file the user opened) could name private content and is not logged.
    private static string GetFileId(string? mimePath)
    {
        if (string.IsNullOrEmpty(mimePath))
            return "-";

        var trimmed = Path.TrimEndingDirectorySeparator(mimePath);
        var name = Path.GetFileName(trimmed);

        if (Guid.TryParse(name, out var folderId))
            return folderId.ToString("N");

        return Guid.TryParse(Path.GetFileName(Path.GetDirectoryName(trimmed)), out var parentId)
            ? parentId.ToString("N")
            : "-";
    }

    private static ReaderRenderMode ToRenderMode(bool isReaderView)
        => isReaderView ? ReaderRenderMode.ReaderView : ReaderRenderMode.Original;

    private static RemoteContentMode ToEngineMode(DomainRemoteContentMode mode) => mode switch
    {
        DomainRemoteContentMode.LoadEverything => RemoteContentMode.LoadEverything,
        DomainRemoteContentMode.BlockTrackers => RemoteContentMode.BlockTrackers,
        _ => RemoteContentMode.BlockAll
    };

    // A remote resource the engine refuses for its kind or because a limit was reached stays unloaded like any other
    // blocked remote content, so the reader page counts it as such. A refused scheme is not remote content.
    private static DomainBlockedResourceReason? ToDomainReason(BlockedResource blocked) => blocked.Reason switch
    {
        BlockedResourceReason.HiddenImage => DomainBlockedResourceReason.HiddenImage,
        BlockedResourceReason.TinyImage => DomainBlockedResourceReason.TinyImage,
        BlockedResourceReason.KnownTracker => DomainBlockedResourceReason.KnownTracker,
        BlockedResourceReason.TrackingPath => DomainBlockedResourceReason.TrackingPath,
        BlockedResourceReason.RemoteContent => DomainBlockedResourceReason.RemoteContent,
        BlockedResourceReason.DisallowedKind or BlockedResourceReason.LimitReached when IsWebUrl(blocked.Url)
            => DomainBlockedResourceReason.RemoteContent,
        _ => null
    };

    private static bool IsWebUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
           (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>Another body for a loaded message: inline resources still come from the message.</summary>
    private sealed class MessageHtmlVariantContent(HtmlMailContent body, IMailContent original) : IMailContent
    {
        public MailBody Body => body.Body;

        public string? Subject => original.Subject;

        public bool TryGetResource(string url, out MailResource resource) => original.TryGetResource(url, out resource);
    }
}
