using System.Collections.Specialized;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.ViewModels;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Mail.Compose;

/// <summary>
/// The composer's signature button (Mac <c>RebuildSignatureMenu</c>): a menu with the composing account's address,
/// "(no signature)", every signature of the account with the one in the draft selected, and "Manage signatures…".
/// A choice swaps only the signature block of the body through the ViewModel.
/// </summary>
internal sealed class ComposeSignatureMenu : IDisposable
{
    private readonly ComposePageViewModel _viewModel;
    private readonly IDispatcher _dispatcher;
    private readonly Func<Task> _manage;
    private readonly Action<Exception> _error;
    private readonly Gtk.Button _button = WinoIcons.Button(WinoIconGlyph.Signature, Translator.SettingsSignature_Title);
    private readonly List<IDisposable> _subscriptions = [];
    private WinoMenuPopover? _open;
    private bool _disposed;

    public ComposeSignatureMenu(ComposePageViewModel viewModel, IDispatcher dispatcher, Func<Task> manage, Action<Exception> error)
    {
        WinoLiveObjects.Track(this);
        _viewModel = viewModel;
        _dispatcher = dispatcher;
        _manage = manage;
        _error = error;
        _button.AddCssClass("wino-editor-tool");
        _button.SetValign(Gtk.Align.Center);
        _subscriptions.Add(GtkSignals.OnClicked(_button, Open));

        System.ComponentModel.PropertyChangedEventHandler changed = (_, args) =>
        {
            if (args.PropertyName is nameof(ComposePageViewModel.SelectedSignature) or nameof(ComposePageViewModel.ComposingAccount)) Post();
        };
        NotifyCollectionChangedEventHandler collection = (_, _) => Post();
        viewModel.PropertyChanged += changed;
        viewModel.AvailableSignatures.CollectionChanged += collection;
        _subscriptions.Add(new ActionDisposable(() =>
        {
            viewModel.PropertyChanged -= changed;
            viewModel.AvailableSignatures.CollectionChanged -= collection;
        }));
        Update();
    }

    public Gtk.Widget Root => _button;
    public WinoMenuPopover? OpenMenu => _open;

    public string AccessibleName
        => $"{Translator.SettingsSignature_Title}: {(_viewModel.SelectedSignature is { } signature ? DisplayName(signature) : Translator.Composer_NoSignature)}";

    public void Open()
    {
        if (_disposed || _viewModel.ComposingAccount is not { } account) return;
        _open?.Popdown();
        var selected = _viewModel.SelectedSignature;
        var builder = WinoMenus.Create();
        builder.Separator(account.Address ?? string.Empty);
        builder.Radio("signature", Translator.Composer_NoSignature, selected is null, () => Apply(null));
        foreach (var signature in _viewModel.AvailableSignatures.ToArray())
        {
            var item = signature;
            builder.Radio("signature", DisplayName(item), selected?.Id == item.Id, () => Apply(item));
        }
        builder.Separator();
        builder.Item(Translator.Composer_ManageSignatures, () => Observe(_manage()), icon: WinoIconGlyph.Settings);
        var popover = _open = ContextMenu.Show(_button, builder.Build(), onClosed: () => _open = null);
        popover.Root.SetPosition(Gtk.PositionType.Bottom);
    }

    private void Apply(AccountSignature? signature)
    {
        if (_disposed) return;
        Observe(_viewModel.ApplySignatureCommand.ExecuteAsync(signature));
    }

    private static string DisplayName(AccountSignature signature)
        => string.IsNullOrWhiteSpace(signature.Name) ? Translator.SettingsSignature_Title : signature.Name;

    private void Post() => _ = _dispatcher.ExecuteOnUIThread(Update);

    private void Update()
    {
        if (_disposed) return;
        var name = AccessibleName;
        _button.SetSensitive(_viewModel.ComposingAccount is not null);
        _button.SetTooltipText(name);
        WinoA11y.Label(_button, name);
    }

    private async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { if (!_disposed) _error(exception); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _open?.Popdown();
        _open = null;
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
    }
}
