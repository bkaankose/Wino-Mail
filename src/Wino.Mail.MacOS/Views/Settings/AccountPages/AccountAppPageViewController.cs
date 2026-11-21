using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.ViewModels.AccountSettings;
using Wino.Presentation.AppKit;
using Wino.Presentation.Bindings;

namespace Wino.Mail.MacOS.Views.Settings.AccountPages;

/// <summary>
/// The page of one app of an account (Windows AccountAppHeaderControl plus the app's own settings): the switch for the
/// app, "Syncs with" with the source and the server, then the app's settings from <see cref="BuildAppSettings"/>.
/// </summary>
public abstract class AccountAppPageViewController<TViewModel>(TViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<TViewModel>(viewModel, dispatcher, logger), ISettingsPageTitleSource
    where TViewModel : AccountAppPageViewModelBase
{
    public string? PageTitle => ViewModel.AppItem.Title;

    public event EventHandler? PageTitleChanged;

    protected override void BuildPage()
    {
        var vm = ViewModel;
        Bind.Bind(vm, nameof(vm.Account), s => s.Account, _ => PageTitleChanged?.Invoke(this, EventArgs.Empty));

        var signIn = InfoBar(WinoInfoBarSeverity.Warning, null, string.Format(Translator.AccountApps_SignInNeededMessage, vm.AppItem.Title));
        signIn.IsClosable = false;
        signIn.ActionTitle = Translator.AccountApps_SignIn;
        signIn.ActionInvoked += (_, _) => { if (vm.SignInAgainCommand.CanExecute(null)) vm.SignInAgainCommand.Execute(null); };
        Bind.Visible(signIn, vm, nameof(vm.NeedsSignIn), s => s.NeedsSignIn);
        Add(signIn);

        var toggle = Bind.Switch(vm.AppItem, nameof(vm.AppItem.IsOn), s => s.IsOn, (s, v) => s.IsOn = v, vm.AppItem.UseTitle);
        Bind.Enabled(toggle, vm.AppItem, nameof(vm.AppItem.CanToggle), s => s.CanToggle);
        var spinner = new NSProgressIndicator { Style = NSProgressIndicatorStyle.Spinning, ControlSize = NSControlSize.Small, Indeterminate = true, TranslatesAutoresizingMaskIntoConstraints = false };
        Bind.Bind(vm.AppItem, nameof(vm.AppItem.IsBusy), s => s.IsBusy, busy =>
        {
            spinner.Hidden = !busy;
            if (busy) spinner.StartAnimation(null); else spinner.StopAnimation(null);
        });
        var switchCard = Card(vm.AppItem.UseTitle, vm.AppItem.UseDescription, vm.AppItem.Icon, Row(spinner, toggle));
        Add(switchCard);

        var lastApp = Caption(Translator.AccountApps_LastAppHint);
        Bind.Visible(lastApp, vm.AppItem, nameof(vm.AppItem.IsLastApp), s => s.IsLastApp);
        Add(lastApp);

        var syncsWith = Group(Translator.AccountApps_SyncsWith, BuildSourceChoice(), BuildSourceText(), BuildServerRow());
        Bind.Visible(syncsWith, vm, nameof(vm.IsOn), s => s.IsOn);
        Add(syncsWith);

        var off = Caption(null);
        off.MaximumNumberOfLines = 0;
        Bind.Bind(vm, nameof(vm.OffNote), s => s.OffNote, text => off.StringValue = text ?? string.Empty);
        Bind.Visible(off, vm, nameof(vm.IsOff), s => s.IsOff);
        Add(off);

        BuildAppSettings();
    }

    /// <summary>The app's own settings below "Syncs with", in the Windows card order.</summary>
    protected abstract void BuildAppSettings();

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
        => ViewModel.InitializeAsync(mode, parameter!);

    private NSView BuildSourceChoice()
    {
        var vm = ViewModel;
        var options = WinoLayout.VStack(10);
        options.Alignment = NSLayoutAttribute.Leading;
        Bind.Visible(options, vm, nameof(vm.IsSourceChoiceVisible), s => s.IsSourceChoiceVisible);

        var buttons = new List<(AccountAppSourceOption Option, NSButton Button)>();
        Bind.Collection(vm.SourceOptions, () =>
        {
            foreach (var view in options.ArrangedSubviews) { options.RemoveArrangedSubview(view); view.RemoveFromSuperview(); }
            buttons.Clear();
            foreach (var option in vm.SourceOptions.ToList())
            {
                var radio = NSButton.CreateRadioButton(option.Title, () =>
                {
                    if (vm.SelectedSourceOption != option) vm.SelectedSourceOption = option;
                });
                radio.State = Equals(vm.SelectedSourceOption, option) ? NSCellStateValue.On : NSCellStateValue.Off;
                WinoAccessibility.Label(radio, option.Title);
                buttons.Add((option, radio));
                var description = Caption(option.Description);
                description.MaximumNumberOfLines = 0;
                var row = WinoLayout.VStack(2, radio, description);
                row.Alignment = NSLayoutAttribute.Leading;
                options.AddArrangedSubview(row);
            }
        });
        Bind.Bind(vm, nameof(vm.SelectedSourceOption), s => s.SelectedSourceOption, selected =>
        {
            foreach (var (option, button) in buttons)
                button.State = Equals(selected, option) ? NSCellStateValue.On : NSCellStateValue.Off;
        });
        Bind.Enabled(options, vm, nameof(vm.IsChangingSource), s => !s.IsChangingSource);
        return options;
    }

    private NSView BuildSourceText()
    {
        var vm = ViewModel;
        var card = Card(vm.SourceText, vm.SourceDescription, WinoIconGlyph.Sync);
        Bind.Bind(vm, nameof(vm.SourceText), s => s.SourceText, text => card.Header = text);
        Bind.Bind(vm, nameof(vm.SourceDescription), s => s.SourceDescription, text => card.Description = text);
        Bind.Visible(card, vm, nameof(vm.IsSourceTextVisible), s => s.IsSourceTextVisible);
        return card;
    }

    private NSView BuildServerRow()
    {
        var vm = ViewModel;
        var status = Bind.Label(vm, nameof(vm.ServerStatusText), s => s.ServerStatusText, WinoStyle.Description, WinoStyle.SecondaryText);
        var action = Bind.Button(vm.ServerActionText, vm.OpenServerSettingsCommand);
        Bind.Bind(vm, nameof(vm.ServerActionText), s => s.ServerActionText, text => action.Title = text ?? string.Empty);
        var card = Card(vm.ServerRowTitle, vm.ServerRowDescription, WinoIconGlyph.GlobeDesktop, Row(status, action));
        Bind.Bind(vm, nameof(vm.ServerRowTitle), s => s.ServerRowTitle, text => card.Header = text);
        Bind.Bind(vm, nameof(vm.ServerRowDescription), s => s.ServerRowDescription, text => card.Description = text);
        Bind.Visible(card, vm, nameof(vm.IsServerRowVisible), s => s.IsServerRowVisible);
        return card;
    }
}
