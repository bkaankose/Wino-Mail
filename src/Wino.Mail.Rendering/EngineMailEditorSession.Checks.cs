using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Reader;
using Wino.Mail.ViewModels;
using Wino.Renderer;
using Wino.Renderer.Editing;

namespace Wino.Mail.Rendering;

public sealed partial class EngineMailEditorSession : IComposeDraftChecks
{
    private bool _pasteAsHtml = true;

    public Task SetPasteFormattingAsync(bool keepFormatting, CancellationToken cancellationToken = default)
        => RunAsync(() =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            _pasteAsHtml = keepFormatting;
            _view.Composer?.PasteAsHtml = keepFormatting;

            return Task.CompletedTask;
        });

    public Task<IReadOnlyList<ComposeDraftIssue>> CheckAsync(bool imageDescriptions, bool readableText, long maximumBytes, CancellationToken cancellationToken = default)
        => RunAsync<IReadOnlyList<ComposeDraftIssue>>(async () =>
        {
            var checks = (imageDescriptions ? DraftChecks.ImageDescription : DraftChecks.None)
                         | (readableText ? DraftChecks.ReadableText : DraftChecks.None)
                         | (maximumBytes > 0 ? DraftChecks.MessageSize : DraftChecks.None);

            if (checks == DraftChecks.None || await WhenIdleAsync(cancellationToken) is not { } composer)
                return Array.Empty<ComposeDraftIssue>();

            return composer.CheckDraft(checks, maximumBytes)
                .Select(static issue => new ComposeDraftIssue(ToKind(issue.Check), issue.Measured, issue.CanFix, issue))
                .ToArray();
        });

    public Task<bool> SelectAsync(ComposeDraftIssue issue, CancellationToken cancellationToken = default)
        => RunAsync(async () =>
        {
            if (issue.Token is not DraftIssue engineIssue || await WhenIdleAsync(cancellationToken) is not { } composer)
                return false;

            var selected = composer.SelectDraftIssue(engineIssue);
            if (selected && !_disposed)
                _view.RequestFocus();

            return selected;
        });

    public Task<bool> FixAsync(ComposeDraftIssue issue, CancellationToken cancellationToken = default)
        => RunAsync(async () =>
            issue.Token is DraftIssue engineIssue && await WhenIdleAsync(cancellationToken) is { } composer && composer.FixDraftIssue(engineIssue));

    public async Task DescribeAsync(ComposeDraftIssue issue, CancellationToken cancellationToken = default)
    {
        if (await SelectAsync(issue, cancellationToken))
            await DescribeSelectedImageAsync(cancellationToken);
    }

    public async Task<bool> MarkDecorativeAsync(ComposeDraftIssue issue, CancellationToken cancellationToken = default)
        => await SelectAsync(issue, cancellationToken) && await RunAsync(() => Task.FromResult(SetSelectedImageDescription(string.Empty)));

    /// <summary>Asks for the description of the selected picture and applies it. An empty answer leaves it undescribed.</summary>
    public Task DescribeSelectedImageAsync(CancellationToken cancellationToken = default)
        => RunAsync(async () =>
        {
            if (await WhenIdleAsync(cancellationToken) is not { State.IsImageSelected: true } composer)
                return;

            var current = composer.State.ImageAltText;
            var answer = await _dialogService.ShowPictureDescriptionDialogAsync(current);
            if (answer is null || _disposed)
                return;

            // A picture already marked as decoration stays so when the user saves no description.
            SetSelectedImageDescription(answer.Length > 0 ? answer : current == string.Empty ? string.Empty : null);
        });

    /// <summary>Asks for a new size of the selected picture and applies it as one undo step.</summary>
    public Task ResizeSelectedImageAsync(CancellationToken cancellationToken = default)
        => RunAsync(async () =>
        {
            if (await WhenIdleAsync(cancellationToken) is not { } composer)
                return;

            var state = composer.State;
            if (!state.IsImageSelected || state.ImageWidthPx is not int width || state.ImageHeightPx is not int height)
                return;

            if (await _dialogService.ShowPictureResizeDialogAsync(new PictureSize(width, height)) is not { } size || _disposed)
                return;

            var current = composer.State;
            if (current.IsImageSelected && current.ImageWidthPx is int currentWidth && current.ImageHeightPx is int currentHeight)
                composer.ResizeSelectedImageBy(Math.Max(1, size.Width) - currentWidth, Math.Max(1, size.Height) - currentHeight);
        });

    private bool SetSelectedImageDescription(string? description)
    {
        if (_view.Composer is not { State: { IsImageSelected: true } state })
            return false;

        return _view.Execute(EditCommandKind.SetImageProperties, new ImagePropertiesArgs(description, state.ImageLinkUrl));
    }

    private static ComposeDraftCheckKind ToKind(DraftChecks check) => check switch
    {
        DraftChecks.ImageDescription => ComposeDraftCheckKind.ImageDescription,
        DraftChecks.ReadableText => ComposeDraftCheckKind.ReadableText,
        _ => ComposeDraftCheckKind.MessageSize
    };
}
