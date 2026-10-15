using System.ComponentModel;
using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using AndroidX.Activity;
using AndroidX.AppCompat.Graphics.Drawable;
using Google.Android.Material.AppBar;
using Google.Android.Material.TextField;
using Wino.Core.Domain;
using Wino.Presentation.Android;
using Wino.Presentation.Android.Bindings;
using Wino.Presentation.Bindings;
using Fragment = AndroidX.Fragment.App.Fragment;

namespace Wino.Mail.Android.Views.Onboarding;

/// <summary>
/// View plumbing the onboarding pages share (canvas Material pass): the top app bar's back arrow, step-aware back,
/// outlined text fields, exposed dropdowns and provider logos. No account rules live here; the shared ViewModels own
/// them.
/// </summary>
internal static class OnboardingViews
{
    /// <summary>The 64 dp top app bar: title and, when <paramref name="navigate"/> is set, the back arrow.</summary>
    public static MaterialToolbar? Toolbar(View root, ViewBinder binder, Action? navigate)
    {
        if (root.FindViewById<MaterialToolbar>(Resource.Id.onboarding_toolbar) is not { Context: { } context } toolbar) return null;
        if (navigate is null) return toolbar;
        toolbar.NavigationIcon = new DrawerArrowDrawable(context) { Color = WinoColors.PrimaryText(context), Progress = 1 };
        toolbar.NavigationContentDescription = Translator.Buttons_Back;
        EventHandler<AndroidX.AppCompat.Widget.Toolbar.NavigationClickEventArgs> handler = (_, _) =>
        {
            try { navigate(); }
            catch (Exception exception) { binder.Error(exception); }
        };
        toolbar.NavigationClick += handler;
        binder.Scope.Own(new ActionDisposable(() => toolbar.NavigationClick -= handler));
        return toolbar;
    }

    /// <summary>
    /// System back goes to <paramref name="back"/> while <paramref name="read"/> is true (a wizard step other than the
    /// first, or a page that must not be left). Registered with the fragment's view lifecycle, so it is removed with the
    /// view and takes precedence over the activity's navigation callback.
    /// </summary>
    public static void InterceptBack<T>(Fragment fragment, ViewBinder binder, T source, string property, Func<T, bool> read, Action back)
        where T : INotifyPropertyChanged
    {
        if (fragment.Activity is not { } activity) return;
        var callback = new BackCallback(back, binder.Error);
        activity.OnBackPressedDispatcher.AddCallback(fragment.ViewLifecycleOwner, callback);
        binder.Bind(source, property, read, enabled => callback.Enabled = enabled);
        binder.Scope.Own(new ActionDisposable(callback.Remove));
    }

    /// <summary>An outlined text field: the label, and two-way text with the ViewModel.</summary>
    public static TextInputLayout? Field<T>(View root, int layoutId, ViewBinder binder, T source, string label, string property,
        Func<T, string?> read, Action<T, string> write) where T : INotifyPropertyChanged
    {
        if (root.FindViewById<TextInputLayout>(layoutId) is not { EditText: { } edit } layout) return null;
        layout.Hint = label;
        binder.TwoWayText(edit, source, property, read, write);
        return layout;
    }

    /// <summary>An exposed dropdown menu over a list of labels, two-way with an index property.</summary>
    public static void Dropdown<T>(View root, int layoutId, ViewBinder binder, T source, string label, IReadOnlyList<string> items,
        string property, Func<T, int> read, Action<T, int> write) where T : INotifyPropertyChanged
    {
        if (root.FindViewById<TextInputLayout>(layoutId) is not { Context: { } context } layout ||
            layout.EditText is not MaterialAutoCompleteTextView field) return;
        layout.Hint = label;
        field.SetSimpleItems(items.ToArray());
        binder.Bind(source, property, read, index =>
        {
            var text = index >= 0 && index < items.Count ? items[index] : string.Empty;
            if (field.Text != text) field.SetText(text, false);
        });
        EventHandler<AdapterView.ItemClickEventArgs> handler = (_, args) =>
        {
            try { write(source, args.Position); }
            catch (Exception exception) { binder.Error(exception); }
        };
        field.ItemClick += handler;
        binder.Scope.Own(new ActionDisposable(() => field.ItemClick -= handler));
    }

    /// <summary>Sets a TextView's text, hiding it while the text is empty.</summary>
    public static void Caption(TextView? view, string? text)
    {
        if (view is null) return;
        view.Text = text ?? string.Empty;
        view.Visibility = string.IsNullOrWhiteSpace(text) ? ViewStates.Gone : ViewStates.Visible;
    }

    public static void Show(View? view, bool visible)
    {
        if (view is not null) view.Visibility = visible ? ViewStates.Visible : ViewStates.Gone;
    }

    private static readonly Dictionary<string, Bitmap> ProviderLogos = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A provider's logo from the packaged Windows provider images (ProviderDetail.ProviderImage, for example
    /// "/Assets/Providers/Outlook.png"), decoded at the row's logo size (the source images are large, and Android 6
    /// keeps bitmaps in the 48 MB Java heap) and kept for the process. Null when the image is missing.
    /// </summary>
    public static Bitmap? ProviderLogo(Context context, string? image)
    {
        if (string.IsNullOrWhiteSpace(image) || context.Assets is not { } assets) return null;
        lock (ProviderLogos)
        {
            if (ProviderLogos.TryGetValue(image, out var cached)) return cached;
        }
        var path = "PackagedResources/" + image.TrimStart('/');
        var target = context.Resources?.GetDimensionPixelSize(Resource.Dimension.onboarding_provider_logo) ?? WinoUnits.Dp(context, 40);
        try
        {
            var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
            using (var probe = assets.Open(path)) BitmapFactory.DecodeStream(probe, null, bounds);
            var sample = 1;
            while (bounds.OutWidth / (sample * 2) >= target && bounds.OutHeight / (sample * 2) >= target) sample *= 2;
            using var stream = assets.Open(path);
            if (BitmapFactory.DecodeStream(stream, null, new BitmapFactory.Options { InSampleSize = sample }) is not { } decoded) return null;
            var bitmap = decoded;
            if (decoded.Width > target || decoded.Height > target)
            {
                var scale = Math.Min((double)target / decoded.Width, (double)target / decoded.Height);
                bitmap = Bitmap.CreateScaledBitmap(decoded, Math.Max(1, (int)(decoded.Width * scale)), Math.Max(1, (int)(decoded.Height * scale)), true);
                if (!ReferenceEquals(bitmap, decoded)) decoded.Recycle();
            }
            Serilog.Log.Debug("Provider logo {Image}: {Width}x{Height} sampled {Sample} to {Final}px", image, bounds.OutWidth, bounds.OutHeight, sample, bitmap.Width);
            lock (ProviderLogos) ProviderLogos[image] = bitmap;
            return bitmap;
        }
        catch (Java.IO.IOException)
        {
            return null;
        }
    }

    private sealed class BackCallback(Action back, Action<Exception> error) : OnBackPressedCallback(false)
    {
        public override void HandleOnBackPressed()
        {
            try { back(); }
            catch (Exception exception) { error(exception); }
        }
    }
}
