#nullable enable
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.AppLock;

namespace Wino.Mail.ViewModels.Data;

/// <summary>One tile of the app lock method picker.</summary>
public sealed partial class AppLockMethodOption(AppLockMethod method, string title, string description, WinoIconGlyph icon) : ObservableObject
{
    public AppLockMethod Method { get; } = method;

    public string Title { get; } = title;

    public WinoIconGlyph Icon { get; } = icon;

    [ObservableProperty]
    public partial string Description { get; set; } = description;

    [ObservableProperty]
    public partial bool IsAvailable { get; set; } = true;

    /// <summary>True for the method the lock currently uses.</summary>
    [ObservableProperty]
    public partial bool IsActive { get; set; }

    public string ActiveText => Translator.AppLock_ActiveMethod;
}
