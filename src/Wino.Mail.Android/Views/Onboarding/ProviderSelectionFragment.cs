using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using Google.Android.Material.Button;
using Google.Android.Material.ImageView;
using Google.Android.Material.MaterialSwitch;
using Google.Android.Material.TextField;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Android;
using Wino.Presentation.Android.Bindings;

namespace Wino.Mail.Android.Views.Onboarding;

/// <summary>
/// Add account (canvas Material pass board AddAccount; Windows ProviderSelectionPage, iOS ProviderSelectionPageViewController):
/// the shared wizard's three steps on one page. Step 1 lists the shared featured providers and the custom server entry
/// (Microsoft, Google, iCloud, Yahoo, Other); a row selects the provider and continues. Step 2 is the account name and
/// colour, step 3 what the account is used for (capability switches and the provider or on-device choice, as in the
/// board's Permissions rows) and the mail download range. Continue then routes as the ViewModel decides: OAuth to account
/// setup, iCloud and Yahoo to their credentials page, a custom server to the server page. The board's "Import from Wino
/// on Windows" row and the searchable provider catalogue are not shown.
/// </summary>
public sealed class ProviderSelectionFragment : WinoFragment<ProviderSelectionPageViewModel>
{
    protected override int LayoutResource => Resource.Layout.fragment_provider_selection;

    protected override void OnBind(View view, ProviderSelectionPageViewModel viewModel, IDispatcher dispatcher)
    {
        var binder = new ViewBinder(Bindings, dispatcher, ReportError);
        WinoInsets.ApplyWithKeyboard(view.FindViewById(Resource.Id.onboarding_insets) ?? view);

        if (OnboardingViews.Toolbar(view, binder, () => GoBack(viewModel)) is { } toolbar)
        {
            binder.Bind(viewModel, nameof(viewModel.PageTitle), vm => vm.PageTitle, title => toolbar.SetHeading(title));
            binder.Bind(viewModel, nameof(viewModel.StepProgressText), vm => vm.StepProgressText, text => toolbar.Subtitle = text);
        }
        // Back walks the wizard's steps; from the first step it leaves the page like the toolbar arrow.
        OnboardingViews.InterceptBack(this, binder, viewModel, nameof(viewModel.CurrentStep),
            vm => vm.CurrentStep != ProviderSelectionWizardStep.Provider, () => GoBack(viewModel));
        if (view.FindViewById<TextView>(Resource.Id.provider_subtitle) is { } subtitle)
            binder.Bind(viewModel, nameof(viewModel.PageSubtitle), vm => vm.PageSubtitle, text => subtitle.Text = text);

        BindProviderStep(view, binder, viewModel);
        BindIdentityStep(view, binder, viewModel);
        BindCapabilityStep(view, binder, viewModel);

        if (view.FindViewById<MaterialButton>(Resource.Id.provider_continue) is { } next)
        {
            binder.Bind(viewModel, nameof(viewModel.ContinueButtonText), vm => vm.ContinueButtonText, text => next.Text = text);
            binder.Command(next, viewModel.ContinueCommand);
        }
        // The provider step continues from its rows.
        if (view.FindViewById(Resource.Id.provider_actions) is { } actions)
            binder.Visible(actions, viewModel, nameof(viewModel.IsProviderStepVisible), vm => !vm.IsProviderStepVisible);
        if (view.FindViewById(Resource.Id.provider_scroll) is { } scroll)
            binder.Bind(viewModel, nameof(viewModel.CurrentStep), vm => vm.CurrentStep, _ => scroll.ScrollTo(0, 0));
    }

    private static void GoBack(ProviderSelectionPageViewModel viewModel)
    {
        if (viewModel.GoBackCommand.CanExecute(null)) viewModel.GoBackCommand.Execute(null);
    }

    private void BindProviderStep(View view, ViewBinder binder, ProviderSelectionPageViewModel viewModel)
    {
        if (view.FindViewById(Resource.Id.provider_step) is { } step)
            binder.Visible(step, viewModel, nameof(viewModel.IsProviderStepVisible), vm => vm.IsProviderStepVisible);
        if (view.FindViewById<LinearLayout>(Resource.Id.provider_list) is not { Context: { } context } list) return;

        void Rebuild()
        {
            list.RemoveAllViews();
            var inflater = LayoutInflater.From(context) ?? throw new InvalidOperationException("No layout inflater.");
            var providers = viewModel.FeaturedProviders.ToList();
            if (viewModel.CustomServerProvider is { } custom && !providers.Contains(custom)) providers.Add(custom);
            foreach (var provider in providers)
            {
                if (inflater.Inflate(Resource.Layout.onboarding_provider_row, list, false) is not { } row) continue;
                if (row.FindViewById<ShapeableImageView>(Resource.Id.provider_logo) is { } logo)
                    logo.SetImageBitmap(OnboardingViews.ProviderLogo(context, provider.ProviderImage));
                if (row.FindViewById<TextView>(Resource.Id.provider_name) is { } name) name.Text = provider.Name;
                if (row.FindViewById<TextView>(Resource.Id.provider_description) is { } description) description.Text = provider.Description;
                if (row.FindViewById<WinoIconView>(Resource.Id.provider_chevron) is { } chevron) chevron.Icon = WinoIconGlyph.ChevronRight;
                row.ContentDescription = $"{provider.Name}, {provider.Description}";
                binder.Click(row, () =>
                {
                    viewModel.SelectProviderCommand.Execute(provider);
                    if (viewModel.ContinueCommand.CanExecute(null)) viewModel.ContinueCommand.Execute(null);
                });
                list.AddView(row);
            }
        }
        binder.Bind(viewModel, nameof(viewModel.FeaturedProviders), vm => vm.FeaturedProviders, _ => Rebuild());
    }

    private void BindIdentityStep(View view, ViewBinder binder, ProviderSelectionPageViewModel viewModel)
    {
        if (view.FindViewById(Resource.Id.identity_step) is { } step)
            binder.Visible(step, viewModel, nameof(viewModel.IsIdentityStepVisible), vm => vm.IsIdentityStepVisible);
        if (OnboardingViews.Field(view, Resource.Id.identity_name_layout, binder, viewModel, Translator.ProviderSelection_AccountNameHeader,
                nameof(viewModel.AccountName), vm => vm.AccountName, (vm, value) => vm.AccountName = value) is { } name)
        {
            name.PlaceholderText = Translator.ProviderSelection_AccountNamePlaceholder;
            name.HelperText = Translator.ProviderSelection_AccountNameDescription;
        }
        if (view.FindViewById<TextView>(Resource.Id.identity_color_title) is { } colorTitle) colorTitle.Text = Translator.ProviderSelection_AccountColorHeader;
        if (view.FindViewById<TextView>(Resource.Id.identity_color_description) is { } colorDescription) colorDescription.Text = Translator.ProviderSelection_AccountColorDescription;
        if (view.FindViewById<LinearLayout>(Resource.Id.identity_colors) is not { Context: { } context } colors) return;

        var swatches = new List<(View View, string? Hex)>();
        void Rebuild()
        {
            colors.RemoveAllViews();
            swatches.Clear();
            AddSwatch(context, colors, swatches, binder, null, () => viewModel.ClearColorCommand.Execute(null));
            foreach (var color in viewModel.AvailableColors)
                AddSwatch(context, colors, swatches, binder, color.Hex, () => viewModel.SelectedColor = color);
            Select(viewModel.SelectedColor?.Hex);
        }
        void Select(string? hex)
        {
            foreach (var (swatch, swatchHex) in swatches)
                swatch.Selected = string.Equals(swatchHex, hex, StringComparison.OrdinalIgnoreCase);
        }
        // AvailableColors is filled on navigation without a change notification, so any change re-reads it.
        binder.BindAny(viewModel, vm => vm.AvailableColors, _ => Rebuild());
        binder.Bind(viewModel, nameof(viewModel.SelectedColor), vm => vm.SelectedColor?.Hex, Select);
    }

    /// <summary>A round colour swatch with a ring when selected; the first one (no colour) clears the choice.</summary>
    private static void AddSwatch(Context context, LinearLayout colors, List<(View View, string? Hex)> swatches, ViewBinder binder, string? hex, Action choose)
    {
        var size = context.Resources?.GetDimensionPixelSize(Resource.Dimension.onboarding_swatch_size) ?? WinoUnits.Dp(context, 36);
        var fill = hex is not null && WinoColors.FromHexString(hex) is { } parsed ? parsed : WinoColors.SubtleFill(context);
        var normal = new GradientDrawable();
        normal.SetShape(ShapeType.Oval);
        normal.SetColor(fill);
        normal.SetStroke(WinoUnits.Dp(context, 1), WinoColors.ZoneStroke(context));
        var selected = new GradientDrawable();
        selected.SetShape(ShapeType.Oval);
        selected.SetColor(fill);
        selected.SetStroke(WinoUnits.Dp(context, 3), WinoColors.Accent(context));
        var states = new StateListDrawable();
        states.AddState([global::Android.Resource.Attribute.StateSelected], selected);
        states.AddState([], normal);

        var swatch = new TextView(context)
        {
            Background = states,
            Gravity = GravityFlags.Center,
            ContentDescription = hex is null ? Translator.ProviderSelection_ClearColor : string.Format(Translator.Accessibility_ColorOption, hex),
            Clickable = true,
            Focusable = true
        };
        if (hex is null) WinoIcons.Apply(swatch, WinoIconGlyph.Dismiss, 14);
        var parameters = new LinearLayout.LayoutParams(size, size) { MarginEnd = WinoUnits.Dp(context, 12) };
        colors.AddView(swatch, parameters);
        swatches.Add((swatch, hex));
        binder.Click(swatch, choose);
    }

    private void BindCapabilityStep(View view, ViewBinder binder, ProviderSelectionPageViewModel viewModel)
    {
        if (view.FindViewById(Resource.Id.capability_step) is { } step)
            binder.Visible(step, viewModel, nameof(viewModel.IsCapabilityStepVisible), vm => vm.IsCapabilityStepVisible);
        var capabilities = viewModel.Capabilities;

        Capability(view.FindViewById(Resource.Id.capability_mail), binder, capabilities, WinoIconGlyph.AppModeMail,
            Translator.ProviderSelection_UseForMail, nameof(capabilities.MailDescription), s => s.MailDescription,
            nameof(capabilities.IsMailEnabled), s => s.IsMailEnabled, (s, v) => s.IsMailEnabled = v, modes: null);
        Capability(view.FindViewById(Resource.Id.capability_calendar), binder, capabilities, WinoIconGlyph.AppModeCalendar,
            Translator.ProviderSelection_UseForCalendar, null, _ => Translator.ProviderSelection_CalendarStepDescription,
            nameof(capabilities.IsCalendarEnabled), s => s.IsCalendarEnabled, (s, v) => s.IsCalendarEnabled = v,
            new ModeBinding(nameof(capabilities.CalendarMode), s => s.CalendarMode, (s, v) => s.CalendarMode = v,
                nameof(capabilities.CalendarProviderOptionText), s => s.CalendarProviderOptionText,
                nameof(capabilities.IsCalendarModeChoiceVisible), s => s.IsCalendarModeChoiceVisible,
                Translator.ProviderSelection_Why_CalendarLocal_Device));
        Capability(view.FindViewById(Resource.Id.capability_contacts), binder, capabilities, WinoIconGlyph.AppModeContacts,
            Translator.ProviderSelection_UseForContacts, null, _ => Translator.ProviderSelection_ContactsStepDescription,
            nameof(capabilities.IsContactEnabled), s => s.IsContactEnabled, (s, v) => s.IsContactEnabled = v,
            new ModeBinding(nameof(capabilities.ContactMode), s => s.ContactMode, (s, v) => s.ContactMode = v,
                nameof(capabilities.ContactProviderOptionText), s => s.ContactProviderOptionText,
                nameof(capabilities.IsContactModeChoiceVisible), s => s.IsContactModeChoiceVisible,
                Translator.ProviderSelection_Why_ContactsLocal_Device));
        Capability(view.FindViewById(Resource.Id.capability_tasks), binder, capabilities, WinoIconGlyph.AppModeToDo,
            Translator.ProviderSelection_UseForTasks, null, _ => Translator.ProviderSelection_TasksStepDescription,
            nameof(capabilities.IsTaskEnabled), s => s.IsTaskEnabled, (s, v) => s.IsTaskEnabled = v,
            new ModeBinding(nameof(capabilities.TaskMode), s => s.TaskMode, (s, v) => s.TaskMode = v,
                nameof(capabilities.TaskProviderOptionText), s => s.TaskProviderOptionText,
                nameof(capabilities.IsTaskModeChoiceVisible), s => s.IsTaskModeChoiceVisible,
                capabilities.TaskLocalOnlyNote));

        var ranges = viewModel.InitialSynchronizationRanges;
        OnboardingViews.Dropdown(view, Resource.Id.capability_range_layout, binder, viewModel, Translator.ProviderSelection_MailRangeHeader,
            ranges.Select(range => range.DisplayText).ToList(), nameof(viewModel.SelectedInitialSynchronizationRange),
            vm => vm.SelectedInitialSynchronizationRange is { } selected ? ranges.IndexOf(selected) : -1,
            (vm, index) => { if (index >= 0 && index < ranges.Count) vm.SelectedInitialSynchronizationRange = ranges[index]; });
        if (view.FindViewById(Resource.Id.capability_range_layout) is { } range)
            binder.Visible(range, viewModel, nameof(viewModel.IsMailSynchronizationRangeVisible), vm => vm.IsMailSynchronizationRangeVisible);
        if (view.FindViewById<TextView>(Resource.Id.capability_range_warning) is { } warning)
        {
            warning.Text = Translator.AccountCreation_InitialSynchronization_EverythingWarning;
            binder.Visible(warning, viewModel, nameof(viewModel.IsInitialSynchronizationWarningVisible), vm => vm.IsInitialSynchronizationWarningVisible);
        }
        if (view.FindViewById<TextView>(Resource.Id.capability_hint) is { } hint)
        {
            // One hint at a time, in the order the iOS capability step lists them.
            binder.BindAny(viewModel, vm => vm.IsLocalOnlyHintVisible ? Translator.ProviderSelection_LocalOnlyHint_Device
                : vm.IsCalendarOnlyServerHintVisible ? Translator.ProviderSelection_CalendarOnlyServerHint
                : vm.IsCardDavDiscoveryHintVisible ? vm.DavContactAvailabilityMessage
                : string.Empty, text => OnboardingViews.Caption(hint, text));
        }
        if (view.FindViewById<TextView>(Resource.Id.capability_missing) is { } missing)
        {
            missing.Text = Translator.ProviderSelection_CapabilityValidationMessage;
            binder.Visible(missing, viewModel, nameof(viewModel.IsCapabilitySelectionMissing), vm => vm.IsCapabilitySelectionMissing);
        }
    }

    private sealed record ModeBinding(
        string ModeProperty, Func<AccountCapabilitySelection, AccountCapabilityMode> ReadMode, Action<AccountCapabilitySelection, AccountCapabilityMode> WriteMode,
        string ProviderTextProperty, Func<AccountCapabilitySelection, string> ProviderText,
        string ChoiceVisibleProperty, Func<AccountCapabilitySelection, bool> ChoiceVisible,
        string LocalOnlyNote);

    /// <summary>One capability row: the switch turns it on or off; the dropdown picks provider or on-device.</summary>
    private static void Capability(View? row, ViewBinder binder, AccountCapabilitySelection capabilities, WinoIconGlyph glyph, string title,
        string? descriptionProperty, Func<AccountCapabilitySelection, string> description,
        string enabledProperty, Func<AccountCapabilitySelection, bool> readEnabled, Action<AccountCapabilitySelection, bool> writeEnabled,
        ModeBinding? modes)
    {
        if (row?.Context is not { } context) return;
        if (row.FindViewById<TextView>(Resource.Id.capability_glyph) is { } icon)
        {
            icon.Typeface = WinoIcons.Colorful(context);
            icon.Text = WinoIcons.Glyph(glyph);
        }
        if (row.FindViewById<TextView>(Resource.Id.capability_title) is { } titleLabel) titleLabel.Text = title;
        if (row.FindViewById<TextView>(Resource.Id.capability_description) is { } descriptionLabel)
        {
            if (descriptionProperty is null) descriptionLabel.Text = description(capabilities);
            else binder.Text(descriptionLabel, capabilities, descriptionProperty, description);
        }
        if (row.FindViewById<MaterialSwitch>(Resource.Id.capability_switch) is { } toggle)
        {
            toggle.ContentDescription = title;
            binder.Checked(toggle, capabilities, enabledProperty, readEnabled, writeEnabled);
        }

        var group = row.FindViewById<TextInputLayout>(Resource.Id.capability_modes);
        var note = row.FindViewById<TextView>(Resource.Id.capability_note);
        if (modes is null)
        {
            OnboardingViews.Show(group, false);
            OnboardingViews.Show(note, false);
            return;
        }
        binder.BindAny(capabilities, s => (Enabled: readEnabled(s), Choice: modes.ChoiceVisible(s)), state =>
        {
            OnboardingViews.Show(group, state.Enabled && state.Choice);
            OnboardingViews.Caption(note, state.Enabled && !state.Choice ? modes.LocalOnlyNote : null);
        });
        if (group?.EditText is not MaterialAutoCompleteTextView field) return;
        group.Hint = title;
        // Provider first, then on this device (the shared AccountCapabilityMode order).
        string[] Items(AccountCapabilitySelection s) => [modes.ProviderText(s), Translator.ProviderSelection_Choice_Local_Device];
        binder.BindAny(capabilities, s => (Provider: modes.ProviderText(s), Mode: modes.ReadMode(s)), state =>
        {
            var items = Items(capabilities);
            field.SetSimpleItems(items);
            var text = state.Mode == AccountCapabilityMode.Local ? items[1] : items[0];
            if (field.Text != text) field.SetText(text, false);
        });
        EventHandler<AdapterView.ItemClickEventArgs> chosen = (_, args) =>
        {
            try { modes.WriteMode(capabilities, args.Position == 1 ? AccountCapabilityMode.Local : AccountCapabilityMode.Provider); }
            catch (Exception exception) { binder.Error(exception); }
        };
        field.ItemClick += chosen;
        binder.Scope.Own(new Wino.Presentation.Android.Bindings.ActionDisposable(() => field.ItemClick -= chosen));
    }
}
