using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.Models.Accounts;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Onboarding;

/// <summary>
/// The setup steps as rows (macOS AccountSetupStepList): a status indicator (an empty circle while pending, a spinner
/// while running, a check when done, a cross on failure), the step title and the failure detail under a failed step.
/// Each row is named by the step's automation name, which carries the status in words. Used by the wizard's progress
/// step and by the standalone progress page.
/// </summary>
internal sealed class AccountSetupStepList
{
    private readonly Gtk.Box _root = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
    private readonly List<StepRow> _rows = [];

    public AccountSetupStepList()
    {
        _root.SetHalign(Gtk.Align.Center);
        _root.AccessibleRole = Gtk.AccessibleRole.List;
    }

    public Gtk.Widget Root => _root;

    public int Count => _rows.Count;

    /// <summary>Mirrors the collection; changes may arrive on any thread and are applied on the main thread.</summary>
    public IDisposable Bind(ObservableCollection<AccountSetupStepModel> steps, IDispatcher dispatcher, Action<Exception> error)
    {
        var disposed = false;
        async void Rebuild()
        {
            try
            {
                await dispatcher.ExecuteOnUIThread(() =>
                {
                    if (disposed) return;
                    Clear();
                    foreach (var step in steps.ToArray())
                    {
                        var row = new StepRow(step, dispatcher, error);
                        _rows.Add(row);
                        _root.Append(row.Root);
                    }
                });
            }
            catch (Exception exception) { if (!disposed) error(exception); }
        }
        NotifyCollectionChangedEventHandler changed = (_, _) => Rebuild();
        steps.CollectionChanged += changed;
        Rebuild();
        return new ActionDisposable(() =>
        {
            disposed = true;
            steps.CollectionChanged -= changed;
            Clear();
        });
    }

    public string Describe() => string.Join(" | ", _rows.Select(row => row.Describe()));

    private void Clear()
    {
        foreach (var row in _rows)
        {
            row.Detach();
            _root.Remove(row.Root);
        }
        _rows.Clear();
    }

    private sealed class StepRow
    {
        private readonly AccountSetupStepModel _step;
        private readonly IDispatcher _dispatcher;
        private readonly Action<Exception> _error;
        private readonly Gtk.Box _row = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
        private readonly WinoIcon _icon = new(WinoIconGlyph.Circle, 16, WinoIconMode.Mono);
        private readonly Adw.Spinner _spinner = OnboardingUi.Spinner();
        private readonly Gtk.Label _title = Gtk.Label.New(string.Empty);
        private readonly Gtk.Label _detail = OnboardingUi.Error();
        private bool _detached;

        public StepRow(AccountSetupStepModel step, IDispatcher dispatcher, Action<Exception> error)
        {
            _step = step;
            _dispatcher = dispatcher;
            _error = error;
            var indicator = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
            indicator.SetSizeRequest(28, 20);
            indicator.SetValign(Gtk.Align.Start);
            indicator.SetMarginTop(2);
            _icon.Root.SetHalign(Gtk.Align.Center);
            _spinner.SetHalign(Gtk.Align.Center);
            indicator.SetHomogeneous(true);
            indicator.Append(_icon.Root);
            indicator.Append(_spinner);
            _title.SetXalign(0);
            _title.SetWrap(true);
            _detail.SetSelectable(true);
            var text = OnboardingUi.Column(2, _title, _detail);
            _row.Append(indicator);
            _row.Append(text);
            _row.SetMarginTop(6);
            _row.SetMarginBottom(6);
            _row.AccessibleRole = Gtk.AccessibleRole.ListItem;
            _step.PropertyChanged += Changed;
            Apply();
        }

        public Gtk.Widget Root => _row;

        public string Describe() => $"{_step.Kind}:{_step.Status}";

        public void Detach()
        {
            _detached = true;
            _step.PropertyChanged -= Changed;
        }

        private async void Changed(object? sender, PropertyChangedEventArgs args)
        {
            try { await _dispatcher.ExecuteOnUIThread(() => { if (!_detached) Apply(); }); }
            catch (Exception exception) { _error(exception); }
        }

        private void Apply()
        {
            OnboardingUi.SetText(_title, _step.Title);
            OnboardingUi.SetText(_detail, _step.ErrorMessage);
            OnboardingUi.SetVisible(_detail, _step.IsFailed && !string.IsNullOrWhiteSpace(_step.ErrorMessage));
            if (_step.IsPending) _title.AddCssClass("dim-label");
            else _title.RemoveCssClass("dim-label");
            WinoA11y.Label(_row, _step.AutomationName);

            OnboardingUi.SetVisible(_icon.Root, !_step.IsInProgress);
            OnboardingUi.SetVisible(_spinner, _step.IsInProgress);
            foreach (var name in new[] { "success", "error", "dim-label" }) _icon.Root.RemoveCssClass(name);
            switch (_step.Status)
            {
                case AccountSetupStepStatus.Succeeded:
                    _icon.SetIcon(WinoIconGlyph.CheckmarkCircleFilled);
                    _icon.Root.AddCssClass("success");
                    break;
                case AccountSetupStepStatus.Failed:
                    _icon.SetIcon(WinoIconGlyph.DismissCircle);
                    _icon.Root.AddCssClass("error");
                    break;
                default:
                    _icon.SetIcon(WinoIconGlyph.Circle);
                    _icon.Root.AddCssClass("dim-label");
                    break;
            }
        }
    }
}

/// <summary>One line of the result under "Account added" (Windows AccountSetupResultRowTemplate).</summary>
internal static class AccountSetupResultRowView
{
    public static Gtk.Widget Create(AccountSetupResultRow row)
    {
        var icon = new WinoIcon(row.Glyph, 16, WinoIconMode.FollowStyle);
        var title = Gtk.Label.New(row.Title);
        title.AddCssClass("heading");
        title.SetValign(Gtk.Align.Start);
        var detail = OnboardingUi.Wrapping(row.Detail);
        detail.AddCssClass("dim-label");
        detail.SetHexpand(true);
        var box = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
        box.SetMarginTop(4);
        box.SetMarginBottom(4);
        icon.Root.SetValign(Gtk.Align.Start);
        box.Append(icon.Root);
        box.Append(title);
        box.Append(detail);
        WinoA11y.Label(box, row.AutomationName);
        return box;
    }
}
