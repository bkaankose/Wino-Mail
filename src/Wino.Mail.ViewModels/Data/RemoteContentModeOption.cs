#nullable enable
using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;

namespace Wino.Mail.ViewModels.Data;

/// <summary>One card of the remote content mode picker, with what the mode loads and blocks.</summary>
public sealed partial class RemoteContentModeOption : ObservableObject
{
    private readonly Action<RemoteContentMode> _select;

    private RemoteContentModeOption(RemoteContentMode mode,
                                    string title,
                                    string tagline,
                                    string[] loads,
                                    string[] blocks,
                                    string note,
                                    bool isRecommended,
                                    Action<RemoteContentMode> select)
    {
        Mode = mode;
        Title = title;
        Tagline = tagline;
        Loads = loads;
        Blocks = blocks;
        Note = note;
        IsRecommended = isRecommended;
        _select = select;
    }

    public RemoteContentMode Mode { get; }
    public string Title { get; }
    public string Tagline { get; }
    public string[] Loads { get; }
    public string[] Blocks { get; }
    public string Note { get; }
    public bool IsRecommended { get; }

    public string LoadsHeader => Translator.PrivacySecurity_LoadsHeader;
    public string BlocksHeader => Translator.PrivacySecurity_BlocksHeader;
    public string RecommendedText => Translator.PrivacySecurity_Recommended;

    /// <summary>Setting it to true selects the mode; the page view model clears the others.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value)
            _select(Mode);
    }

    public static RemoteContentModeOption[] CreateAll(Action<RemoteContentMode> select) =>
    [
        new(RemoteContentMode.LoadEverything,
            Translator.PrivacySecurity_ModeLoadEverything_Title,
            Translator.PrivacySecurity_ModeLoadEverything_Tagline,
            [
                Translator.PrivacySecurity_ModeLoadEverything_Loads1,
                Translator.PrivacySecurity_ModeLoadEverything_Loads2,
                Translator.PrivacySecurity_ModeLoadEverything_Loads3
            ],
            [Translator.PrivacySecurity_ModeLoadEverything_Blocks1],
            Translator.PrivacySecurity_ModeLoadEverything_Note,
            false,
            select),
        new(RemoteContentMode.BlockTrackers,
            Translator.PrivacySecurity_ModeBlockTrackers_Title,
            Translator.PrivacySecurity_ModeBlockTrackers_Tagline,
            [
                Translator.PrivacySecurity_ModeBlockTrackers_Loads1,
                Translator.PrivacySecurity_ModeBlockTrackers_Loads2
            ],
            [
                Translator.PrivacySecurity_ModeBlockTrackers_Blocks1,
                Translator.PrivacySecurity_ModeBlockTrackers_Blocks2
            ],
            Translator.PrivacySecurity_ModeBlockTrackers_Note,
            true,
            select),
        new(RemoteContentMode.BlockAll,
            Translator.PrivacySecurity_ModeBlockAll_Title,
            Translator.PrivacySecurity_ModeBlockAll_Tagline,
            [
                Translator.PrivacySecurity_ModeBlockAll_Loads1,
                Translator.PrivacySecurity_ModeBlockAll_Loads2
            ],
            [
                Translator.PrivacySecurity_ModeBlockAll_Blocks1,
                Translator.PrivacySecurity_ModeBlockAll_Blocks2,
                Translator.PrivacySecurity_ModeBlockAll_Blocks3
            ],
            Translator.PrivacySecurity_ModeBlockAll_Note,
            false,
            select)
    ];
}
