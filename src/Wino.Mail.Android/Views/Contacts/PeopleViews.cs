using System.ComponentModel;
using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using Google.Android.Material.Button;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.ViewModels.Data;
using Wino.Mail.Android.Infrastructure;
using Wino.Presentation.Android;
using Wino.Presentation.Bindings;

namespace Wino.Mail.Android.Views.Contacts;

/// <summary>A state panel (layout people_state_panel): glyph, title, message and an optional action.</summary>
internal sealed class PeopleStatePanel
{
    private readonly View _root;
    private readonly WinoIconView? _glyph;
    private readonly TextView? _title;
    private readonly TextView? _message;
    private readonly MaterialButton? _action;
    private Action? _run;

    public PeopleStatePanel(View root, WinoIconGlyph glyph)
    {
        _root = root;
        var context = root.RequireContext();
        _glyph = root.FindViewById<WinoIconView>(Resource.Id.people_state_glyph);
        _title = root.FindViewById<TextView>(Resource.Id.people_state_title);
        _message = root.FindViewById<TextView>(Resource.Id.people_state_message);
        _action = root.FindViewById<MaterialButton>(Resource.Id.people_state_action);
        if (_glyph is not null)
        {
            _glyph.Icon = glyph;
            _glyph.GlyphSize = WinoTokens.Dp(context, Resource.Dimension.wino_glyph_hero);
        }
        if (_action is not null) _action.Click += (_, _) => _run?.Invoke();
    }

    public bool Visible
    {
        get => _root.Visibility == ViewStates.Visible;
        set => _root.Visibility = value ? ViewStates.Visible : ViewStates.Gone;
    }

    public void Apply(string? title, string? message, string? action = null, Action? run = null)
    {
        if (_title is not null)
        {
            _title.Text = title ?? string.Empty;
            _title.Visibility = string.IsNullOrWhiteSpace(title) ? ViewStates.Gone : ViewStates.Visible;
        }
        if (_message is not null)
        {
            _message.Text = message ?? string.Empty;
            _message.Visibility = string.IsNullOrWhiteSpace(message) ? ViewStates.Gone : ViewStates.Visible;
        }
        _run = run;
        if (_action is not null)
        {
            _action.Text = action ?? string.Empty;
            _action.Visibility = string.IsNullOrWhiteSpace(action) || run is null ? ViewStates.Gone : ViewStates.Visible;
            WinoMaterialAccent.Tint(_action, filled: false);
        }
    }
}

/// <summary>Helpers the Contacts and To Do pages share.</summary>
internal static class PeopleViews
{
    /// <summary>
    /// The Windows ModeReadinessPanel over a mode's zone: covers the content while the mode cannot be used (no account,
    /// access not granted, sign-in required) with the shared title, message and primary action.
    /// </summary>
    public static void BindReadiness(BindingScope bindings, View host, View panelView, ModeReadinessViewModel readiness, IDispatcher dispatcher, WinoIconGlyph glyph)
    {
        host.SetBackgroundColor(WinoColors.ZoneFill(host.RequireContext()));
        var panel = new PeopleStatePanel(panelView, glyph);
        void Apply()
        {
            host.Visibility = readiness.IsBlocked ? ViewStates.Visible : ViewStates.Gone;
            panel.Visible = readiness.IsBlocked;
            if (!readiness.IsBlocked) return;
            panel.Apply(readiness.Title, readiness.Message, readiness.IsPrimaryActionVisible ? readiness.PrimaryActionText : null, () =>
            {
                if (readiness.PrimaryActionCommand.CanExecute(null)) readiness.PrimaryActionCommand.Execute(null);
            });
        }
        PropertyChangedEventHandler changed = (_, _) => _ = dispatcher.ExecuteOnUIThread(Apply);
        readiness.PropertyChanged += changed;
        bindings.Own(new ActionDisposable(() => readiness.PropertyChanged -= changed));
        Apply();
    }

    /// <summary>A stored contact picture, decoded once per file id; null falls back to initials.</summary>
    public static Bitmap? LoadPicture(IPictureStorageService? pictures, Dictionary<Guid, Bitmap?> cache, Guid? fileId, Action<Exception> error)
    {
        if (pictures is null || fileId is not { } id) return null;
        if (cache.TryGetValue(id, out var cached)) return cached;
        Bitmap? image = null;
        try
        {
            var path = pictures.GetPicturePath(PictureKind.Contact, id);
            if (File.Exists(path)) image = BitmapFactory.DecodeFile(path);
        }
        catch (Exception exception) { error(exception); }
        cache[id] = image;
        return image;
    }

    public static async void Observe(Task? task, Action<Exception> error)
    {
        if (task is null) return;
        try { await task; }
        catch (Exception exception) { error(exception); }
    }
}
