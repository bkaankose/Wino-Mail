using System;
using System.Globalization;
using System.IO;
using System.Linq;
using CommunityToolkit.WinUI.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.Text;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.MailItem;
using Wino.Mail.Controls.AccountIcon;
using Wino.Mail.Controls.Core;
using Wino.Mail.Controls.Core.AccountIcon;
using Wino.Mail.Controls.Core.HoverActions;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.WinUI;
using Wino.Mail.WinUI.Controls;

namespace Wino.Helpers;

public static class XamlHelpers
{
    private static CultureInfo AppDisplayCulture => CultureInfo.DefaultThreadCurrentUICulture ?? CultureInfo.CurrentUICulture;
    private static IPreferencesService? PreferencesService => WinoApplication.Current.Services.GetService<IPreferencesService>();
    private static IContactPictureFileService? ContactPictureFileService => WinoApplication.Current.Services.GetService<IContactPictureFileService>();
    private static IAccountProfilePictureFileService AccountProfilePictureFileService => WinoApplication.Current.Services.GetRequiredService<IAccountProfilePictureFileService>();

    #region Mail Filter Editor

    public static string GetFilterFieldGlyph(MailFilterConditionField field) => field switch
    {
        MailFilterConditionField.FromAddress => "\uE715",
        MailFilterConditionField.FromName => "\uE77B",
        MailFilterConditionField.Subject => "\uE8BD",
        MailFilterConditionField.PreviewText => "\uE7C3",
        MailFilterConditionField.HasAttachments => "\uE723",
        MailFilterConditionField.Importance => "\uE8C9",
        _ => "\uE71C"
    };

    public static string GetFilterActionGlyph(MailFilterActionType action) => action switch
    {
        MailFilterActionType.Move => "\uE8DE",
        MailFilterActionType.Archive => "\uE7B8",
        MailFilterActionType.SoftDelete => "\uE74D",
        MailFilterActionType.HardDelete => "\uE74D",
        MailFilterActionType.MarkRead => "\uE8C3",
        MailFilterActionType.MarkUnread => "\uE715",
        MailFilterActionType.SetFlag => "\uE7C1",
        MailFilterActionType.ClearFlag => "\uE894",
        MailFilterActionType.MoveToJunk => "\uE730",
        MailFilterActionType.MarkAsNotJunk => "\uE8FB",
        _ => "\uE945"
    };

    private static Brush GetThemeBrush(string key, string fallbackKey = "AccentFillColorDefaultBrush")
    {
        if (Application.Current.Resources.TryGetValue(key, out var resource) && resource is Brush brush)
            return brush;

        return Application.Current.Resources[fallbackKey] as Brush;
    }

    public static Brush GetFilterActionIconBackground(MailFilterActionType action) => GetThemeBrush(action switch
    {
        MailFilterActionType.MarkRead or MailFilterActionType.MarkAsNotJunk => "SystemFillColorSuccessBackgroundBrush",
        MailFilterActionType.SetFlag or MailFilterActionType.ClearFlag => "SystemFillColorCautionBackgroundBrush",
        MailFilterActionType.MoveToJunk or MailFilterActionType.SoftDelete or MailFilterActionType.HardDelete => "SystemFillColorCriticalBackgroundBrush",
        _ => "SystemFillColorAttentionBackgroundBrush"
    }, "SubtleFillColorSecondaryBrush");

    public static Brush GetFilterActionIconForeground(MailFilterActionType action) => GetThemeBrush(action switch
    {
        MailFilterActionType.MarkRead or MailFilterActionType.MarkAsNotJunk => "SystemFillColorSuccessBrush",
        MailFilterActionType.SetFlag or MailFilterActionType.ClearFlag => "SystemFillColorCautionBrush",
        MailFilterActionType.MoveToJunk or MailFilterActionType.SoftDelete or MailFilterActionType.HardDelete => "SystemFillColorCriticalBrush",
        _ => "SystemFillColorAttentionBrush"
    });

    // Daily briefing tiles: one Fluent system fill pair per semantic tone, so categories stay
    // colorful without hand-picked colors that break in high contrast.
    public static Brush GetBriefingToneBackground(DailyBriefingTone tone) => GetThemeBrush(tone switch
    {
        DailyBriefingTone.Critical => "SystemFillColorCriticalBackgroundBrush",
        DailyBriefingTone.Caution => "SystemFillColorCautionBackgroundBrush",
        DailyBriefingTone.Success => "SystemFillColorSuccessBackgroundBrush",
        DailyBriefingTone.Attention => "SystemFillColorAttentionBackgroundBrush",
        _ => "SubtleFillColorSecondaryBrush"
    }, "SubtleFillColorSecondaryBrush");

    public static Brush GetBriefingToneForeground(DailyBriefingTone tone) => GetThemeBrush(tone switch
    {
        DailyBriefingTone.Critical => "SystemFillColorCriticalBrush",
        DailyBriefingTone.Caution => "SystemFillColorCautionBrush",
        DailyBriefingTone.Success => "SystemFillColorSuccessBrush",
        DailyBriefingTone.Attention => "SystemFillColorAttentionBrush",
        _ => "TextFillColorSecondaryBrush"
    }, "TextFillColorSecondaryBrush");

    public static Brush GetBriefingPriorityBorderBrush(bool isPriority)
        => GetThemeBrush(isPriority ? "SystemFillColorCriticalBackgroundBrush" : "CardStrokeColorDefaultBrush", "CardStrokeColorDefaultBrush");

    public static Brush GetSelectionBorderBrush(bool isSelected)
        => GetThemeBrush(isSelected ? "AccentFillColorDefaultBrush" : "CardStrokeColorDefaultBrush");

    public static int GetManagementCardSpan(bool isProviderAvailable) => isProviderAvailable ? 1 : 2;

    #endregion

    #region Converters

    public static Thickness GetMailItemControlMargin(bool isDisplayedInThread) => isDisplayedInThread ? new Thickness(40, 0, 6, 0) : new Thickness(6, 0, 6, 0);
    public static Thickness GetCompactMailListRowMargin(global::Wino.Mail.Controls.Core.MailListRowKind kind)
        => kind == global::Wino.Mail.Controls.Core.MailListRowKind.ThreadChild ? new Thickness(20, 0, 0, 0) : new Thickness(0);
    public static Thickness GetDetailedMailListRowMargin(global::Wino.Mail.Controls.Core.MailListRowKind kind)
        => kind == global::Wino.Mail.Controls.Core.MailListRowKind.ThreadChild ? new Thickness(24, 0, 0, 0) : new Thickness(0);
    public static bool IsThreadMessageHead(global::Wino.Mail.Controls.Core.MailListRowKind kind)
        => kind == global::Wino.Mail.Controls.Core.MailListRowKind.ThreadHead;
    public static bool IsMultiple(int count) => count > 1;
    public static bool ReverseIsMultiple(int count) => count < 1;
    public static PopupPlacementMode GetPlaccementModeForCalendarType(CalendarDisplayType type)
    {
        return type switch
        {
            CalendarDisplayType.Week => PopupPlacementMode.Right,
            _ => PopupPlacementMode.Bottom,
        };
    }

    public static Visibility ReverseBoolToVisibilityConverter(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility BoolToVisibilityConverter(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public static Thickness GetCalendarPaneItemMargin(bool isPaneCompact) => isPaneCompact ? new Thickness(0) : new Thickness(8, 0, 8, 0);
    public static Visibility ReverseVisibilityConverter(Visibility visibility) => visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    public static bool ReverseBoolConverter(bool value) => !value;
    public static GridLength DoubleToGridLength(double value) => new(value);
    public static bool AreEqual(int value1, int value2) => value1 == value2;
    public static bool ShouldDisplayPreview(string text) => text == null ? false : text.Any(x => char.IsLetter(x));
    public static bool CountToBooleanConverter(int value) => value > 0;
    public static bool ObjectEquals(object obj1, object obj2) => object.Equals(obj1, obj2);
    public static Visibility CountToVisibilityConverter(int value) => value > 0 ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility CountToVisibilityConverterWithThreshold(int value, int threshold) => value > threshold ? Visibility.Visible : Visibility.Collapsed;
    public static ListViewSelectionMode BoolToSelectionMode(bool isSelectionMode) => isSelectionMode ? ListViewSelectionMode.Extended : ListViewSelectionMode.Single;
    public static string BoolToSelectionModeText(bool isSelectionMode) => isSelectionMode ? Translator.Buttons_Cancel : Translator.Buttons_Multiselect;
    public static string ConditionalString(bool condition, string trueValue, string falseValue) => condition ? trueValue : falseValue;

    // Contacts
    public static string GetFavoriteGlyph(bool isFavorite) => isFavorite ? "\uE735" : "\uE734";
    public static string GetFavoriteTooltip(bool isFavorite) => isFavorite ? Translator.ContactAction_Unfavorite : Translator.ContactAction_Favorite;
    public static Brush GetFavoriteBrush(bool isFavorite)
        => (Brush)Application.Current.Resources[isFavorite ? "SystemFillColorCautionBrush" : "TextFillColorSecondaryBrush"];
    public static bool HasText(string value) => !string.IsNullOrWhiteSpace(value);
    public static ContactPhoneKind[] GetPhoneKinds() => Enum.GetValues<ContactPhoneKind>();
    /// <summary>Each postal address slot is labelled with the glyph for its kind.</summary>
    public static string GetPostalAddressKindGlyph(ContactPostalAddressKind kind) => kind switch
    {
        ContactPostalAddressKind.Home => "",
        ContactPostalAddressKind.Business => "",
        _ => "",
    };

    /// <summary>Section header badges show how many entries a collapsed section holds.</summary>
    public static string CountToText(int value) => value.ToString();

    // To Do
    // The star reuses the contacts favorite idiom: filled glyph when set, outline when not.
    public static string GetTaskImportanceGlyph(bool isImportant) => isImportant ? "\uE735" : "\uE734";
    public static Brush GetTaskImportanceBrush(bool isImportant)
        => (Brush)Application.Current.Resources[isImportant ? "SystemFillColorCautionBrush" : "TextFillColorTertiaryBrush"];

    /// <summary>Overdue due-date text turns critical; everything else stays secondary.</summary>
    public static Brush GetTaskDueBrush(bool isOverdue)
        => (Brush)Application.Current.Resources[isOverdue ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush"];

    public static TextDecorations GetTaskTitleDecorations(bool isCompleted)
        => isCompleted ? TextDecorations.Strikethrough : TextDecorations.None;

    public static Brush GetTaskTitleBrush(bool isCompleted)
        => (Brush)Application.Current.Resources[isCompleted ? "TextFillColorTertiaryBrush" : "TextFillColorPrimaryBrush"];

    /// <summary>x:Bind cannot convert double to GridLength, so the drawer width comes through here.</summary>
    public static GridLength GetTaskDrawerWidth(bool isOpen, bool isCompactLayout)
        => isOpen
            ? (isCompactLayout ? new GridLength(1, GridUnitType.Star) : new GridLength(340))
            : new GridLength(0);

    public static GridLength GetTaskListWidth(bool isDrawerOpen, bool isCompactLayout)
        => isDrawerOpen && isCompactLayout ? new GridLength(0) : new GridLength(1, GridUnitType.Star);

    public static Brush GetMyDayBrush(bool isInMyDay)
        => (Brush)Application.Current.Resources[isInMyDay ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush"];

    public static string GetCompletedGroupCaretGlyph(bool isExpanded) => isExpanded ? "\uE70E" : "\uE70D";
    public static Visibility TextToVisibility(string value) => string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility NotNullToVisibility(object value) => value is null ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility CountToInvertedVisibility(int count) => count > 0 ? Visibility.Collapsed : Visibility.Visible;

    // x:Bind cannot nest function calls, so birthday formatting and its visibility are separate flat helpers.
    public static Visibility BirthdayVisibility(int? year, int? month, int? day)
        => string.IsNullOrWhiteSpace(FormatBirthday(year, month, day)) ? Visibility.Collapsed : Visibility.Visible;

    public static string FormatBirthday(int? year, int? month, int? day)
    {
        if (month is not (>= 1 and <= 12) || day is not (>= 1 and <= 31)) return string.Empty;
        var date = new DateTime(year ?? 2000, month.Value, Math.Min(day.Value, DateTime.DaysInMonth(year ?? 2000, month.Value)));
        return year.HasValue ? date.ToString("d MMMM yyyy") : date.ToString("d MMMM");
    }
    public static bool GetGravatarEnabled() => PreferencesService?.IsGravatarEnabled ?? true;
    public static bool GetFaviconEnabled() => PreferencesService?.IsFaviconEnabled ?? true;

    /// <summary>
    /// Every shell pane draws account identity at one size, so a row reads the same
    /// in mail, contacts and tasks.
    /// </summary>
    public const double ShellAccountIconSize = 28d;

    public static IAccountIconInfo? GetAccountIconInfo(MailAccount? account)
        => account is null
            ? null
            : MailAccountIconInfoFactory.Create(account, AccountProfilePictureFileService);

    public static IAccountIconInfo GetAccountIconInfo(
        MailAccount? account,
        MailProviderType providerType,
        SpecialImapProvider specialImapProvider)
        => account is null
            ? MailAccountIconInfoFactory.CreateProviderFallback(providerType, specialImapProvider)
            : MailAccountIconInfoFactory.Create(account, AccountProfilePictureFileService);

    public static IconElement GetAccountOrGlyphIcon(MailAccount? account, string glyph)
        => account is null
            ? new FontIcon { FontSize = 15, Glyph = glyph }
            : new WinoAccountIcon
            {
                Account = GetAccountIconInfo(account),
                IconSize = ShellAccountIconSize
            };

    public static object GetContactPicture(
        AccountContact? contact,
        string? displayName,
        string? address)
    {
        var resolvedName = !string.IsNullOrWhiteSpace(contact?.Name)
            ? contact.Name
            : displayName ?? string.Empty;
        var resolvedAddress = !string.IsNullOrWhiteSpace(contact?.Address)
            ? contact.Address
            : address ?? string.Empty;
        var localImagePath = contact?.ContactPictureFileId is Guid fileId
            ? ContactPictureFileService?.GetContactPicturePath(fileId)
            : null;

        return new ContactPictureIdentity(resolvedName, resolvedAddress, localImagePath);
    }

    public static object GetContactPicture(IMailItemDisplayInformation? item)
        => item is null
            ? new ContactPictureIdentity(string.Empty, string.Empty)
            : GetContactPicture(item.SenderContact, item.FromName, item.FromAddress);

    /// <summary>
    /// Resolves the avatar identity from a projected row's untyped source item. The mail list
    /// row templates bind through this so the avatar lives in the container's own compiled
    /// template, which is the only place <c>x:Phase</c> actually defers work.
    /// </summary>
    public static object GetRowContactPicture(object? sourceItem)
        => GetContactPicture(sourceItem as IMailItemDisplayInformation);

    public static object GetContactPicture(IContactDisplayItem? item)
        => item is null
            ? new ContactPictureIdentity(string.Empty, string.Empty)
            : GetContactPicture(item.PreviewContact, item.DisplayName, item.Address);

    public static Microsoft.UI.Xaml.Media.Imaging.BitmapImage? Base64ToBitmapImage(string base64String)
    {
        if (string.IsNullOrEmpty(base64String))
            return null;

        try
        {
            var imageBytes = Convert.FromBase64String(base64String);
            using var stream = new System.IO.MemoryStream(imageBytes);
            var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            bitmap.SetSource(stream.AsRandomAccessStream());
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    public static Microsoft.UI.Xaml.Media.Imaging.BitmapImage? GetContactEditorPreviewPicture(byte[]? imageBytes, string? imagePath)
    {
        try
        {
            var bytes = imageBytes;
            if ((bytes is null || bytes.Length == 0) && !string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath))
                bytes = File.ReadAllBytes(imagePath);

            if (bytes is null || bytes.Length == 0)
                return null;

            using var stream = new MemoryStream(bytes);
            var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            bitmap.SetSource(stream.AsRandomAccessStream());
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    public static Microsoft.UI.Xaml.Media.Imaging.BitmapImage? StringToBitmapImage(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
            return null;

        try
        {
            var uri = imagePath.StartsWith("/")
                ? new Uri($"ms-appx://{imagePath}")
                : new Uri(imagePath, UriKind.Absolute);

            return new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(uri);
        }
        catch
        {
            return null;
        }
    }

    public static InfoBarSeverity InfoBarSeverityConverter(InfoBarMessageType messageType)
    {
        return messageType switch
        {
            InfoBarMessageType.Information => InfoBarSeverity.Informational,
            InfoBarMessageType.Success => InfoBarSeverity.Success,
            InfoBarMessageType.Warning => InfoBarSeverity.Warning,
            InfoBarMessageType.Error => InfoBarSeverity.Error,
            _ => InfoBarSeverity.Informational,
        };
    }

    public static string InfoBarMessageTypeGlyph(InfoBarMessageType messageType) => messageType switch
    {
        InfoBarMessageType.Success => "",
        InfoBarMessageType.Warning => "",
        InfoBarMessageType.Error => "",
        _ => "",
    };

    public static Brush InfoBarMessageTypeBrush(InfoBarMessageType messageType)
    {
        var key = messageType switch
        {
            InfoBarMessageType.Success => "SystemFillColorSuccessBrush",
            InfoBarMessageType.Warning => "SystemFillColorCautionBrush",
            InfoBarMessageType.Error => "SystemFillColorCriticalBrush",
            _ => "TextFillColorPrimaryBrush",
        };

        return (Brush)Application.Current.Resources[key];
    }

    /// <summary>
    /// Fill of the hero state dot on the intelligence page. Unlike an InfoBar the
    /// neutral state is the accent colour, because the dot has no other meaning.
    /// </summary>
    public static Brush IntelligenceHeroStateBrush(InfoBarMessageType messageType)
    {
        var key = messageType switch
        {
            InfoBarMessageType.Success => "SystemFillColorSuccessBrush",
            InfoBarMessageType.Warning => "SystemFillColorCautionBrush",
            InfoBarMessageType.Error => "SystemFillColorCriticalBrush",
            _ => "AccentFillColorDefaultBrush",
        };

        return (Brush)Application.Current.Resources[key];
    }

    /// <summary>
    /// Indents a row by a computed amount. The view model works in effective pixels because
    /// <see cref="Thickness"/> is a XAML type it cannot reference.
    /// </summary>
    public static Thickness LeftMargin(double left) => new(left, 0, 0, 0);

    public static SolidColorBrush GetReadableTextColor(string backgroundColor)
    {
        if (!backgroundColor.StartsWith("#")) throw new ArgumentException("Hex color must start with #.");

        backgroundColor = backgroundColor.TrimStart('#');

        if (backgroundColor.Length == 6)
        {
            var r = int.Parse(backgroundColor.Substring(0, 2), NumberStyles.HexNumber);
            var g = int.Parse(backgroundColor.Substring(2, 2), NumberStyles.HexNumber);
            var b = int.Parse(backgroundColor.Substring(4, 2), NumberStyles.HexNumber);

            // Calculate relative luminance
            double luminance = (0.2126 * GetLinearValue(r)) +
                               (0.7152 * GetLinearValue(g)) +
                               (0.0722 * GetLinearValue(b));

            return luminance > 0.5 ? new SolidColorBrush(Colors.Black) : new SolidColorBrush(Colors.White);
        }
        else
        {
            throw new ArgumentException("Hex color must be 6 characters long (e.g., #RRGGBB).");
        }
    }

    private static double GetLinearValue(int colorComponent)
    {
        double sRGB = colorComponent / 255.0;
        return sRGB <= 0.03928 ? sRGB / 12.92 : Math.Pow((sRGB + 0.055) / 1.055, 2.4);
    }

    public static Microsoft.UI.Xaml.Controls.NavigationViewDisplayMode NavigationViewDisplayModeConverter(SplitViewDisplayMode splitViewDisplayMode)
    {
        return splitViewDisplayMode switch
        {
            SplitViewDisplayMode.CompactOverlay => Microsoft.UI.Xaml.Controls.NavigationViewDisplayMode.Compact,
            SplitViewDisplayMode.CompactInline => Microsoft.UI.Xaml.Controls.NavigationViewDisplayMode.Minimal,
            SplitViewDisplayMode.Overlay => Microsoft.UI.Xaml.Controls.NavigationViewDisplayMode.Expanded,
            SplitViewDisplayMode.Inline => Microsoft.UI.Xaml.Controls.NavigationViewDisplayMode.Expanded,
            _ => Microsoft.UI.Xaml.Controls.NavigationViewDisplayMode.Minimal,
        };
    }

    public static string GetColorFromHex(Color color) => color.ToHex();
    public static Color GetWindowsColorFromHex(string hex) => hex.ToColor();

    public static SolidColorBrush GetSolidColorBrushFromHex(string? colorHex)
        => string.IsNullOrWhiteSpace(colorHex)
            ? GetDefaultBrushForUnderlyingTheme()
            : new SolidColorBrush(colorHex.ToColor());

    private static SolidColorBrush GetDefaultBrushForUnderlyingTheme()
        => new(Wino.Mail.WinUI.WinoApplication.Current.UnderlyingThemeService.IsUnderlyingThemeDark()
            ? Colors.White
            : Colors.Black);

    public static SolidColorBrush GetCategoryTextBrush(string textColorHex, string backgroundColorHex)
        => !string.IsNullOrWhiteSpace(textColorHex)
            ? GetSolidColorBrushFromHex(textColorHex)
            : string.IsNullOrWhiteSpace(backgroundColorHex)
                ? new SolidColorBrush(Colors.Black)
                : GetReadableTextColor(backgroundColorHex);
    public static FontWeight GetFontWeightBySyncState(bool isSyncing) => isSyncing ? FontWeights.SemiBold : FontWeights.Normal;

    public static Brush GetWizardStepBadgeBrush(bool isActive)
        => isActive
            ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
            : new SolidColorBrush(Color.FromArgb(30, 128, 128, 128));

    public static Brush GetWizardStepNumberForeground(bool isActive)
        => isActive
            ? new SolidColorBrush(Colors.White)
            : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

    public static Brush GetChoiceCardBorderBrush(bool isSelected)
        => (Brush)Application.Current.Resources[isSelected
            ? "AccentFillColorDefaultBrush"
            : "CardStrokeColorDefaultBrush"];

    public static Thickness GetChoiceCardBorderThickness(bool isSelected) => new(isSelected ? 2 : 1);

    /// <summary>
    /// Collapses a grid column completely when the element inside it is hidden.
    /// </summary>
    public static GridLength GetColumnWidthByVisibility(bool isVisible)
        => isVisible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
    public static FontWeight GetFontWeightByChildSelectedState(bool isChildSelected) => isChildSelected ? FontWeights.SemiBold : FontWeights.Normal;
    public static FontWeight GetFontWeightByReadState(bool isChildSelected) => isChildSelected ? FontWeights.Normal : FontWeights.SemiBold;
    public static FontWeight GetMailItemSenderFontWeightByReadState(bool isRead) => isRead ? FontWeights.Normal : FontWeights.Bold;
    public static HoverActionKind GetHoverActionKind(MailOperation operation) => operation switch
    {
        MailOperation.Archive => HoverActionKind.Archive,
        MailOperation.SoftDelete => HoverActionKind.Delete,
        MailOperation.SetFlag or MailOperation.ClearFlag => HoverActionKind.ToggleFlag,
        MailOperation.MarkAsRead or MailOperation.MarkAsUnread => HoverActionKind.ToggleRead,
        MailOperation.MoveToJunk => HoverActionKind.MoveToJunk,
        _ => HoverActionKind.None,
    };

    public static HoverActionAnimation GetHoverActionAnimation(MailHoverActionAnimation animation) => animation switch
    {
        MailHoverActionAnimation.Slide => HoverActionAnimation.Slide,
        MailHoverActionAnimation.NoAnimation => HoverActionAnimation.NoAnimation,
        _ => HoverActionAnimation.Popup,
    };

    public static HoverActionPosition GetHoverActionPosition(MailHoverActionPosition position) => position switch
    {
        MailHoverActionPosition.RightTop => HoverActionPosition.RightTop,
        MailHoverActionPosition.RightBottom => HoverActionPosition.RightBottom,
        MailHoverActionPosition.TopCenter => HoverActionPosition.TopCenter,
        MailHoverActionPosition.BottomCenter => HoverActionPosition.BottomCenter,
        _ => HoverActionPosition.RightCenter,
    };

    public static HoverActionLabels GetHoverActionLabels() => new(
        Translator.HoverActionOption_Archive,
        Translator.HoverActionOption_Delete,
        Translator.HoverActionOption_ToggleFlag,
        Translator.HoverActionOption_ToggleRead,
        Translator.HoverActionOption_MoveJunk);

    public static Visibility StringToVisibilityConverter(string value) => string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility StringToVisibilityReversedConverter(string value) => string.IsNullOrWhiteSpace(value) ? Visibility.Visible : Visibility.Collapsed;
    public static bool IsAccountNicknameVisible(string accountNickname, AccountNicknamePosition position, AccountNicknamePosition targetPosition)
        => !string.IsNullOrWhiteSpace(accountNickname) && position == targetPosition;
    public static string GetDraftTagText() => $"[{Translator.Draft}]";
    public static string GetMailItemSubjectForListing(string? subject) => string.IsNullOrWhiteSpace(subject) ? $"({Translator.MailItemNoSubject})" : subject;
    public static TimeFormatPreference CurrentMailTimeFormatPreference
        => PreferencesService?.MailTimeFormatPreference ?? TimeFormatPreference.UseLanguageCulture;

    public static string GetMailItemDisplaySummaryForListing(bool isDraft, DateTime receivedDate, TimeFormatPreference timeFormatPreference)
    {
        if (isDraft)
            return Translator.Draft;
        else
        {
            var localTime = receivedDate.ToLocalTime();
            var displayType = DateTimeDisplayFormatter.GetTimeDisplayType(timeFormatPreference, AppDisplayCulture);

            return DateTimeDisplayFormatter.FormatTime(localTime, displayType, AppDisplayCulture);
        }
    }

    public static Visibility GetMailItemPreviewTextVisibility(string text)
        => (PreferencesService?.IsShowPreviewEnabled ?? true) && ShouldDisplayPreview(text) ? Visibility.Visible : Visibility.Collapsed;

    public static bool GetMailItemSenderPictureVisibility()
        => PreferencesService?.IsShowSenderPicturesEnabled ?? true;

    public static string GetCreationDateString(DateTime date, TimeFormatPreference timeFormatPreference)
    {
        var localTime = date.ToLocalTime();
        var displayType = DateTimeDisplayFormatter.GetTimeDisplayType(timeFormatPreference, AppDisplayCulture);
        return $"{localTime.ToString("D", AppDisplayCulture)} {DateTimeDisplayFormatter.FormatTime(localTime, displayType, AppDisplayCulture)}";
    }
    public static string GetMailGroupDateString(object groupObject)
    {
        if (groupObject is global::Wino.Mail.Controls.Core.MailListGroup mailListGroup)
        {
            groupObject = mailListGroup.Key;
        }

        if (groupObject is global::Wino.Mail.Controls.Core.MailListProjectionGroupKey projectionGroupKey)
        {
            if (projectionGroupKey.IsPinned)
                return Translator.FolderCustomization_SectionPinned;

            groupObject = projectionGroupKey.Value!;
        }

        if (groupObject is MailListGroupKey pinnedGroupKey)
        {
            if (pinnedGroupKey.IsPinned)
                return Translator.FolderCustomization_SectionPinned;

            groupObject = pinnedGroupKey.Value!;
        }

        if (groupObject is string stringObject)
            return stringObject;

        object? dateObject = null;

        // From regular mail header template
        if (groupObject is DateTime groupedDate)
            dateObject = groupedDate;
        else if (groupObject is IGrouping<object, MailCopy> groupKey)
        {
            // From semantic group header.
            dateObject = groupKey.Key;
        }

        if (dateObject != null)
        {
            if (dateObject is DateTime dateTimeValue)
            {
                if (dateTimeValue == DateTime.Today)
                    return Translator.Today;
                else if (dateTimeValue == DateTime.Today.AddDays(-1))
                    return Translator.Yesterday;
                else
                {
                    return dateTimeValue.ToString("D", AppDisplayCulture);
                }

            }
            else
                return dateObject.ToString() ?? string.Empty;
        }

        return Translator.UnknownDateHeader;
    }

    public static string GetBreadcrumbStepAutomationName(int stepNumber, string title)
        => stepNumber > 0
            ? string.Format(Translator.Accessibility_BreadcrumbStep, stepNumber, title)
            : title;

    public static string GetAccountSetupStepAutomationName(AccountSetupStepModel? step)
        => step == null
            ? string.Empty
            : string.Format(Translator.Accessibility_AccountSetupStep, step.Title, GetAccountSetupStepStatusText(step.Status));

    public static string GetMailAddressAutomationName(string label, string? displayName, string? address)
    {
        var normalizedLabel = (label ?? string.Empty).Trim().TrimEnd(':');
        var normalizedAddress = address?.Trim() ?? string.Empty;
        var normalizedDisplayName = displayName?.Trim() ?? string.Empty;
        var contactText = string.IsNullOrWhiteSpace(normalizedDisplayName)
            ? normalizedAddress
            : string.Equals(normalizedDisplayName, normalizedAddress, StringComparison.OrdinalIgnoreCase)
                ? normalizedAddress
                : $"{normalizedDisplayName} <{normalizedAddress}>";

        return string.IsNullOrWhiteSpace(normalizedLabel)
            ? contactText
            : $"{normalizedLabel}: {contactText}";
    }

    public static string GetCalendarsForAccountAutomationName(string? accountName)
        => string.Format(Translator.Accessibility_CalendarsForAccount, accountName ?? string.Empty);

    public static string GetCalendarToggleAutomationName(string? calendarName)
        => string.Format(Translator.Accessibility_CalendarToggle, calendarName ?? string.Empty);

    private static string GetAccountSetupStepStatusText(AccountSetupStepStatus status)
        => status switch
        {
            AccountSetupStepStatus.Pending => Translator.Accessibility_SetupStepPending,
            AccountSetupStepStatus.InProgress => Translator.Accessibility_SetupStepInProgress,
            AccountSetupStepStatus.Succeeded => Translator.Accessibility_SetupStepSucceeded,
            AccountSetupStepStatus.Failed => Translator.Accessibility_SetupStepFailed,
            _ => string.Empty
        };


    #endregion

    #region Wino Font Icon Transformation

    public static WinoIconGlyph GetWinoIconGlyph(FilterOptionType type) => type switch
    {
        FilterOptionType.All => WinoIconGlyph.Mail,
        FilterOptionType.Unread => WinoIconGlyph.MarkUnread,
        FilterOptionType.Flagged => WinoIconGlyph.Flag,
        FilterOptionType.Mentions => WinoIconGlyph.NewMail,
        FilterOptionType.Files => WinoIconGlyph.Attachment,
        _ => WinoIconGlyph.None,
    };

    public static WinoIconGlyph GetWinoIconGlyph(SortingOptionType type) => type switch
    {
        SortingOptionType.Sender => WinoIconGlyph.SortTextDesc,
        SortingOptionType.ReceiveDate => WinoIconGlyph.SortLinesDesc,
        _ => WinoIconGlyph.None,
    };

    public static WinoIconGlyph GetWinoIconGlyph(MailOperation operation)
    {
        return operation switch
        {
            MailOperation.None => WinoIconGlyph.None,
            MailOperation.Archive => WinoIconGlyph.Archive,
            MailOperation.UnArchive => WinoIconGlyph.UnArchive,
            MailOperation.SoftDelete => WinoIconGlyph.Delete,
            MailOperation.HardDelete => WinoIconGlyph.Delete,
            MailOperation.Move => WinoIconGlyph.Forward,
            MailOperation.MoveToJunk => WinoIconGlyph.Blocked,
            MailOperation.MoveToFocused => WinoIconGlyph.None,
            MailOperation.MoveToOther => WinoIconGlyph.None,
            MailOperation.AlwaysMoveToOther => WinoIconGlyph.None,
            MailOperation.AlwaysMoveToFocused => WinoIconGlyph.None,
            MailOperation.SetFlag => WinoIconGlyph.Flag,
            MailOperation.ClearFlag => WinoIconGlyph.ClearFlag,
            MailOperation.MarkAsRead => WinoIconGlyph.MarkRead,
            MailOperation.MarkAsUnread => WinoIconGlyph.MarkUnread,
            MailOperation.MarkAsNotJunk => WinoIconGlyph.Blocked,
            MailOperation.Ignore => WinoIconGlyph.Ignore,
            MailOperation.Reply => WinoIconGlyph.Reply,
            MailOperation.ReplyAll => WinoIconGlyph.ReplyAll,
            MailOperation.Zoom => WinoIconGlyph.Zoom,
            MailOperation.SaveAs => WinoIconGlyph.Save,
            MailOperation.SaveAsPdf => WinoIconGlyph.Save,
            MailOperation.SaveAsEml => WinoIconGlyph.ViewMessageSource,
            MailOperation.Print => WinoIconGlyph.Print,
            MailOperation.Find => WinoIconGlyph.Find,
            MailOperation.Forward => WinoIconGlyph.Forward,
            MailOperation.DarkEditor => WinoIconGlyph.DarkEditor,
            MailOperation.LightEditor => WinoIconGlyph.LightEditor,
            MailOperation.ViewMessageSource => WinoIconGlyph.ViewMessageSource,
            _ => WinoIconGlyph.None,
        };
    }

    public static WinoIconGlyph GetPathGeometry(FolderOperation operation)
    {
        return operation switch
        {
            FolderOperation.None => WinoIconGlyph.None,
            FolderOperation.Pin => WinoIconGlyph.Pin,
            FolderOperation.Unpin => WinoIconGlyph.UnPin,
            FolderOperation.MarkAllAsRead => WinoIconGlyph.MarkRead,
            FolderOperation.DontSync => WinoIconGlyph.DontSync,
            FolderOperation.Empty => WinoIconGlyph.EmptyFolder,
            FolderOperation.Rename => WinoIconGlyph.Rename,
            FolderOperation.Delete => WinoIconGlyph.Delete,
            FolderOperation.Move => WinoIconGlyph.Forward,
            FolderOperation.TurnOffNotifications => WinoIconGlyph.TurnOfNotifications,
            FolderOperation.CreateSubFolder => WinoIconGlyph.CreateFolder,
            _ => WinoIconGlyph.None,
        };
    }

    // Segoe Fluent icon glyphs for the show/hide toggle on the folder
    // customization page. E7B3 = "Hide" (eye with slash), E7B2 = "RedEye".
    public static string GetHideGlyph(bool isHidden) => isHidden ? "\uE7B3" : "\uE7B2";

    public static WinoIconGlyph GetSpecialFolderPathIconGeometry(SpecialFolderType specialFolderType)
    {
        return specialFolderType switch
        {
            SpecialFolderType.Inbox => WinoIconGlyph.SpecialFolderInbox,
            SpecialFolderType.Starred => WinoIconGlyph.SpecialFolderStarred,
            SpecialFolderType.Important => WinoIconGlyph.SpecialFolderImportant,
            SpecialFolderType.Sent => WinoIconGlyph.SpecialFolderSent,
            SpecialFolderType.Draft => WinoIconGlyph.SpecialFolderDraft,
            SpecialFolderType.Archive => WinoIconGlyph.SpecialFolderArchive,
            SpecialFolderType.Deleted => WinoIconGlyph.SpecialFolderDeleted,
            SpecialFolderType.Junk => WinoIconGlyph.SpecialFolderJunk,
            SpecialFolderType.Chat => WinoIconGlyph.SpecialFolderChat,
            SpecialFolderType.Category => WinoIconGlyph.SpecialFolderCategory,
            SpecialFolderType.Unread => WinoIconGlyph.SpecialFolderUnread,
            SpecialFolderType.Forums => WinoIconGlyph.SpecialFolderForums,
            SpecialFolderType.Updates => WinoIconGlyph.SpecialFolderUpdated,
            SpecialFolderType.Personal => WinoIconGlyph.SpecialFolderPersonal,
            SpecialFolderType.Promotions => WinoIconGlyph.SpecialFolderPromotions,
            SpecialFolderType.Social => WinoIconGlyph.SpecialFolderSocial,
            SpecialFolderType.Other => WinoIconGlyph.SpecialFolderOther,
            SpecialFolderType.More => WinoIconGlyph.SpecialFolderMore,
            _ => WinoIconGlyph.None,
        };
    }


    public static WinoIconGlyph GetProviderIcon(MailProviderType providerType, SpecialImapProvider specialImapProvider)
    {
        if (specialImapProvider == SpecialImapProvider.None)
        {
            return providerType switch
            {
                MailProviderType.Outlook => WinoIconGlyph.Microsoft,
                MailProviderType.Gmail => WinoIconGlyph.Google,
                MailProviderType.IMAP4 => WinoIconGlyph.IMAP,
                _ => WinoIconGlyph.None,
            };
        }
        else
        {
            return specialImapProvider switch
            {
                SpecialImapProvider.iCloud => WinoIconGlyph.Apple,
                SpecialImapProvider.Yahoo => WinoIconGlyph.Yahoo,
                _ => WinoIconGlyph.None,
            };
        }
    }
    public static WinoIconGlyph GetProviderIcon(MailAccount account)
        => GetProviderIcon(account.ProviderType, account.SpecialImapProvider);

    /// <summary>
    /// Builds a Geometry from path markup held as a string. Named apart from the
    /// FolderOperation overload because x:Bind resolves helper functions by name alone and
    /// cannot choose between overloads.
    /// </summary>
    public static Geometry GetGeometryFromPathMarkup(string pathMarkup)
    {
        string xaml =
        "<Path " +
        "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
        "<Path.Data>" + pathMarkup + "</Path.Data></Path>";
        var path = XamlReader.Load(xaml) as Microsoft.UI.Xaml.Shapes.Path;
        if (path?.Data == null)
        {
            return new PathGeometry();
        }

        Geometry geometry = path.Data;
        path.Data = null;
        return geometry;
    }

    #endregion

    #region Internationalization

    public static string GetOperationString(MailOperation operation)
    {
        return operation switch
        {
            MailOperation.None => "unknown",
            MailOperation.Archive => Translator.MailOperation_Archive,
            MailOperation.UnArchive => Translator.MailOperation_Unarchive,
            MailOperation.SoftDelete => Translator.MailOperation_Delete,
            MailOperation.HardDelete => Translator.MailOperation_Delete,
            MailOperation.Move => Translator.MailOperation_Move,
            MailOperation.MoveToJunk => Translator.MailOperation_MarkAsJunk,
            MailOperation.MoveToFocused => Translator.MailOperation_MoveFocused,
            MailOperation.MoveToOther => Translator.MailOperation_MoveOther,
            MailOperation.AlwaysMoveToOther => Translator.MailOperation_AlwaysMoveOther,
            MailOperation.AlwaysMoveToFocused => Translator.MailOperation_AlwaysMoveFocused,
            MailOperation.SetFlag => Translator.MailOperation_SetFlag,
            MailOperation.ClearFlag => Translator.MailOperation_ClearFlag,
            MailOperation.MarkAsRead => Translator.MailOperation_MarkAsRead,
            MailOperation.MarkAsUnread => Translator.MailOperation_MarkAsUnread,
            MailOperation.MarkAsNotJunk => Translator.MailOperation_MarkNotJunk,
            MailOperation.Seperator => string.Empty,
            MailOperation.Ignore => Translator.MailOperation_Ignore,
            MailOperation.Reply => Translator.MailOperation_Reply,
            MailOperation.ReplyAll => Translator.MailOperation_ReplyAll,
            MailOperation.Zoom => Translator.MailOperation_Zoom,
            MailOperation.SaveAs => Translator.MailOperation_SaveAs,
            MailOperation.SaveAsPdf => Translator.Buttons_PDF,
            MailOperation.SaveAsEml => Translator.Buttons_EML,
            MailOperation.Find => Translator.MailOperation_Find,
            MailOperation.Forward => Translator.MailOperation_Forward,
            MailOperation.DarkEditor => string.Empty,
            MailOperation.LightEditor => string.Empty,
            MailOperation.Print => Translator.MailOperation_Print,
            MailOperation.ViewMessageSource => Translator.MailOperation_ViewMessageSource,
            MailOperation.RetryDraftUpload => Translator.Draft_RetryUpload,
            MailOperation.Navigate => Translator.MailOperation_Navigate,
            _ => "unknown",
        };
    }

    public static string GetOperationString(FolderOperation operation)
    {
        return operation switch
        {
            FolderOperation.None => string.Empty,
            FolderOperation.Pin => Translator.FolderOperation_Pin,
            FolderOperation.Unpin => Translator.FolderOperation_Unpin,
            FolderOperation.MarkAllAsRead => Translator.FolderOperation_MarkAllAsRead,
            FolderOperation.DontSync => Translator.FolderOperation_DontSync,
            FolderOperation.Empty => Translator.FolderOperation_Empty,
            FolderOperation.Rename => Translator.FolderOperation_Rename,
            FolderOperation.Delete => Translator.FolderOperation_Delete,
            FolderOperation.Move => Translator.FolderOperation_Move,
            FolderOperation.CreateSubFolder => Translator.FolderOperation_CreateSubFolder,
            _ => string.Empty,
        };
    }

    #endregion
}
