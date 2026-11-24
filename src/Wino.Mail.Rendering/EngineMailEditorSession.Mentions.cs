using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;
using Wino.Mail.ViewModels;
using Wino.Renderer;
using Wino.Renderer.Editing;

namespace Wino.Mail.Rendering;

/// <summary>
/// "@" mentions. The engine finds the query, writes the person and holds the user's settings; this maps them to the
/// ViewModel's seam.
/// </summary>
public sealed partial class EngineMailEditorSession : IComposeMentions
{
    private MentionOptions _mentionOptions = new();

    public event EventHandler<ComposeMentionQuery?>? MentionQueryChanged;
    public event EventHandler<ComposeMentionKey>? MentionKeyPressed;

    public Task SetMentionSettingsAsync(bool isEnabled, ComposerMentionFormat format, ComposerMentionRecipient addTo, CancellationToken cancellationToken = default)
        => RunAsync(() =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            _mentionOptions = new MentionOptions
            {
                Enabled = isEnabled,
                Format = format switch
                {
                    ComposerMentionFormat.Name => MentionFormat.Name,
                    ComposerMentionFormat.Address => MentionFormat.Address,
                    _ => MentionFormat.NameAndAddress
                },
                AddTo = addTo switch
                {
                    ComposerMentionRecipient.To => MentionRecipient.To,
                    ComposerMentionRecipient.Cc => MentionRecipient.Cc,
                    ComposerMentionRecipient.Bcc => MentionRecipient.Bcc,
                    _ => MentionRecipient.None
                }
            };

            _view.Composer?.Mentions = _mentionOptions;
            return Task.CompletedTask;
        });

    public Task<ComposerMentionRecipient?> InsertMentionAsync(string name, string? address, CancellationToken cancellationToken = default)
        => RunAsync(() =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_view.Composer is not { } composer || !_view.Execute(EditCommandKind.InsertMention, new MentionArgs(name, address)))
                return Task.FromResult<ComposerMentionRecipient?>(null);

            return Task.FromResult<ComposerMentionRecipient?>(composer.MentionRecipientFor(address) switch
            {
                MentionRecipient.To => ComposerMentionRecipient.To,
                MentionRecipient.Cc => ComposerMentionRecipient.Cc,
                MentionRecipient.Bcc => ComposerMentionRecipient.Bcc,
                _ => ComposerMentionRecipient.None
            });
        });

    public Task DismissMentionAsync(CancellationToken cancellationToken = default)
        => RunAsync(() =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _view.Composer?.DismissMention();
            return Task.CompletedTask;
        });

    public Task SetMentionListOpenAsync(bool isOpen, CancellationToken cancellationToken = default)
        => RunAsync(() =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_view.Composer is { } composer)
                composer.MentionListOpen = isOpen;

            return Task.CompletedTask;
        });

    private void Composer_MentionContextChanged(object? sender, MentionContext? context)
        => MentionQueryChanged?.Invoke(this, context is null
            ? null
            : new ComposeMentionQuery(context.Query, context.ViewAnchor.X, context.ViewAnchor.Y, context.ViewAnchor.Width, context.ViewAnchor.Height));

    private void Composer_MentionKeyPressed(object? sender, MentionKey key)
        => MentionKeyPressed?.Invoke(this, key switch
        {
            MentionKey.Up => ComposeMentionKey.Up,
            MentionKey.Down => ComposeMentionKey.Down,
            MentionKey.Accept => ComposeMentionKey.Accept,
            _ => ComposeMentionKey.Cancel
        });
}
