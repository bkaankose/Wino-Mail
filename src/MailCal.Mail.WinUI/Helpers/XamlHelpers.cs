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
using MailCal.Core.Domain;
using MailCal.Core.Domain.Entities.Mail;
using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Domain.Models.Accounts;
using MailCal.Core.Domain.Models.MailItem;
using MailCal.Mail.Controls.AccountIcon;
using MailCal.Mail.Controls.Core;
using MailCal.Mail.Controls.Core.AccountIcon;
using MailCal.Mail.Controls.Core.HoverActions;
using MailCal.Mail.Controls.HoverActions;
using MailCal.Mail.ViewModels.Data;
using MailCal.Mail.WinUI;
using MailCal.Mail.WinUI.Controls;
using MailCal.Services;
using WinRT;

namespace MailCal.Helpers;

public static class XamlHelpers
{
    private static CultureInfo AppDisplayCulture => CultureInfo.DefaultThreadCurrentUICulture ?? CultureInfo.CurrentUICulture;
    private static IPreferencesService? PreferencesService => MailCalApplication.Current.Services.GetService<IPreferencesService>();
    private static IPictureStorageService PictureStorageService => MailCalApplication.Current.Services.GetRequiredService<IPictureStorageService>();
    private static AccountSenderPictureDirectory AccountSenderPictureDirectory => MailCalApplication.Current.Services.GetRequiredService<AccountSenderPictureDirectory>();

    #region Mail Filter Editor

    public static MailCalIconGlyph GetFilterFieldGlyph(MailFilterConditionField field) => field switch
    {
        MailFilterConditionField.FromAddress => MailCalIconGlyph.Mail,
        MailFilterConditionField.FromName => MailCalIconGlyph.Person,
        MailFilterConditionField.Subject => MailCalIconGlyph.Message,
        MailFilterConditionField.PreviewText => MailCalIconGlyph.TextDescription,
        MailFilterConditionField.HasAttachments => MailCalIconGlyph.Attachment,
        MailFilterConditionField.Importance => MailCalIconGlyph.Important,
        _ => MailCalIconGlyph.Filter,
    };

    public static MailCalIconGlyph GetFilterActionGlyph(MailFilterActionType action) => action switch
    {
        MailFilterActionType.Move => MailCalIconGlyph.Move,
        MailFilterActionType.Archive => MailCalIconGlyph.Archive,
        MailFilterActionType.SoftDelete => MailCalIconGlyph.Delete,
        MailFilterActionType.HardDelete => MailCalIconGlyph.Delete,
        MailFilterActionType.MarkRead => MailCalIconGlyph.MarkRead,
        MailFilterActionType.MarkUnread => MailCalIconGlyph.MarkUnread,
        MailFilterActionType.SetFlag => MailCalIconGlyph.Flag,
        MailFilterActionType.ClearFlag => MailCalIconGlyph.ClearFlag,
        MailFilterActionType.MoveToJunk => MailCalIconGlyph.Blocked,
        MailFilterActionType.MarkAsNotJunk => MailCalIconGlyph.Checkmark,
        _ => MailCalIconGlyph.Sparkle,
    };

    [DynamicWindowsRuntimeCast(typeof(Brush))]
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

    public static Brush GetSelectionBorderBrush(bool isSelected)
        => GetThemeBrush(isSelected ? "AccentFillColorDefaultBrush" : "CardStrokeColorDefaultBrush");

    public static int GetManagementCardSpan(bool isProviderAvailable) => isProviderAvailable ? 1 : 2;

    #endregion

    #region Converters

    public static Thickness GetMailItemControlMargin(bool isDisplayedInThread) => isDisplayedInThread ? new Thickness(40, 0, 6, 0) : new Thickness(6, 0, 6, 0);
    public static Thickness GetCompactMailListRowMargin(global::MailCal.Mail.Controls.Core.MailListRowKind kind)
        => kind == global::MailCal.Mail.Controls.Core.MailListRowKind.ThreadChild ? new Thickness(20, 0, 0, 0) : new Thickness(0);
    public static Thickness GetDetailedMailListRowMargin(global::MailCal.Mail.Controls.Core.MailListRowKind kind)
        => kind == global::MailCal.Mail.Controls.Core.MailListRowKind.ThreadChild ? new Thickness(24, 0, 0, 0) : new Thickness(0);
    public static bool IsThreadMessageHead(global::MailCal.Mail.Controls.Core.MailListRowKind kind)
        => kind == global::MailCal.Mail.Controls.Core.MailListRowKind.ThreadHead;
    public static MailCalIconGlyph GetThreadChevronGlyph(bool isExpanded) => isExpanded ? MailCalIconGlyph.ChevronDown : MailCalIconGlyph.ChevronRight;
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
    public static bool IsMailSearchScope(MailSearchScope scope, string expected) => scope.ToString() == expected;
    public static bool ObjectEquals(object obj1, object obj2) => object.Equals(obj1, obj2);
    public static Visibility CountToVisibilityConverter(int value) => value > 0 ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility CountToVisibilityConverterWithThreshold(int value, int threshold) => value > threshold ? Visibility.Visible : Visibility.Collapsed;
    public static ListViewSelectionMode BoolToSelectionMode(bool isSelectionMode) => isSelectionMode ? ListViewSelectionMode.Extended : ListViewSelectionMode.Single;
    public static string BoolToSelectionModeText(bool isSelectionMode) => isSelectionMode ? Translator.Buttons_Cancel : Translator.Buttons_Multiselect;
    public static string ConditionalString(bool condition, string trueValue, string falseValue) => condition ? trueValue : falseValue;

    // Contacts
    // The favorite star itself is drawn by the pages: a brush resolved here would come from the
    // application dictionary and follow the system theme instead of the page theme.
    /// <summary>Chevron of an expandable pane entry: up while expanded, down while collapsed.</summary>
    public static string GetExpanderGlyph(bool isExpanded) => isExpanded ? "\uE70E" : "\uE70D";

    public static string GetFavoriteTooltip(bool isFavorite) => isFavorite ? Translator.ContactAction_Unfavorite : Translator.ContactAction_Favorite;
    public static bool HasText(string value) => !string.IsNullOrWhiteSpace(value);
    public static ContactPhoneKind[] GetPhoneKinds() => Enum.GetValues<ContactPhoneKind>();
    /// <summary>Each postal address slot is labelled with the glyph for its kind.</summary>
    public static MailCalIconGlyph GetPostalAddressKindGlyph(ContactPostalAddressKind kind) => kind switch
    {
        ContactPostalAddressKind.Home => MailCalIconGlyph.Home,
        ContactPostalAddressKind.Business => MailCalIconGlyph.Briefcase,
        _ => MailCalIconGlyph.Location,
    };

    /// <summary>Section header badges show how many entries a collapsed section holds.</summary>
    public static string CountToText(int value) => value.ToString();

    // To Do
    // The star reuses the contacts favorite idiom: filled glyph when set, outline when not.
    public static MailCalIconGlyph GetTaskImportanceGlyph(bool isImportant) => isImportant ? MailCalIconGlyph.StarFilled : MailCalIconGlyph.Star;

    /// <summary>
    /// Task state never resolves a brush here. A brush taken from the application dictionary
    /// carries the application theme instead of the theme of the element that draws it, so a
    /// dark shell painted these with the light palette. State-dependent colors now live on the
    /// page as <c>{ThemeResource}</c> values, and task state only picks which element is shown.
    /// </summary>
    public static Visibility GetTaskDueVisibility(bool hasDueDate, bool isOverdue)
        => hasDueDate && !isOverdue ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility GetTaskOverdueVisibility(bool hasDueDate, bool isOverdue)
        => hasDueDate && isOverdue ? Visibility.Visible : Visibility.Collapsed;

    public static TextDecorations GetTaskTitleDecorations(bool isCompleted)
        => isCompleted ? TextDecorations.Strikethrough : TextDecorations.None;

    /// <summary>A completed title keeps the theme's own text color and recedes with opacity.</summary>
    public static double GetTaskTitleOpacity(bool isCompleted) => isCompleted ? 0.6 : 1d;

    /// <summary>x:Bind cannot convert double to GridLength, so the drawer width comes through here.</summary>
    public static GridLength GetTaskDrawerWidth(bool isOpen, bool isCompactLayout)
        => isOpen
            ? (isCompactLayout ? new GridLength(1, GridUnitType.Star) : new GridLength(340))
            : new GridLength(0);

    public static GridLength GetTaskListWidth(bool isDrawerOpen, bool isCompactLayout)
        => isDrawerOpen && isCompactLayout ? new GridLength(0) : new GridLength(1, GridUnitType.Star);

    /// <summary>x:Bind will not widen the step counts to the double the ProgressBar expects.</summary>
    public static double GetTaskStepCountValue(int count) => count;

    public static MailCalIconGlyph GetCompletedGroupCaretGlyph(bool isExpanded) => isExpanded ? MailCalIconGlyph.ChevronUp : MailCalIconGlyph.ChevronDown;
    public static Visibility TextToVisibility(string value) => string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility NotNullToVisibility(object value) => value is null ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility CountToInvertedVisibility(int count) => count > 0 ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility NullToVisibility(object value) => value is null ? Visibility.Visible : Visibility.Collapsed;

    // Profile editor footer: visible while saving or when the ViewModel left a message behind.
    public static Visibility GetProfileStatusVisibility(bool isBusy, string message) => isBusy || !string.IsNullOrWhiteSpace(message) ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility GetProfileSavedVisibility(string message) => message == Translator.MailCalAccount_Profile_Saved ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility GetProfileFailedVisibility(string message) => !string.IsNullOrWhiteSpace(message) && message != Translator.MailCalAccount_Profile_Saved ? Visibility.Visible : Visibility.Collapsed;
    public static string GetProfileStatusText(bool isBusy, string message) => isBusy ? Translator.MailCalAccount_Profile_Saving : message;

    // Contact detail helpers take the contact itself: x:Bind skips a function call when a leaf argument is null,
    // so binding nullable fields directly would keep the previous contact's value on screen.
    public static Visibility ContactBirthdayVisibility(AccountContact? contact)
        => string.IsNullOrWhiteSpace(FormatContactBirthday(contact)) ? Visibility.Collapsed : Visibility.Visible;

    public static string FormatContactBirthday(AccountContact? contact)
        => contact is null ? string.Empty : FormatBirthday(contact.BirthdayYear, contact.BirthdayMonth, contact.BirthdayDay);

    public static Visibility ContactWebsiteVisibility(AccountContact? contact) => TextToVisibility(contact?.Website);
    public static Visibility ContactNotesVisibility(AccountContact? contact) => TextToVisibility(contact?.Notes);

    public static string FormatBirthday(int? year, int? month, int? day)
    {
        if (month is not (>= 1 and <= 12) || day is not (>= 1 and <= 31)) return string.Empty;
        var date = new DateTime(year ?? 2000, month.Value, Math.Min(day.Value, DateTime.DaysInMonth(year ?? 2000, month.Value)));
        return year.HasValue ? date.ToString("d MMMM yyyy") : date.ToString("d MMMM");
    }

    /// <summary>
    /// Every shell pane draws account identity at one size, so a row reads the same
    /// in mail, contacts and tasks.
    /// </summary>
    public const double ShellAccountIconSize = 28d;

    public static IAccountIconInfo? GetAccountIconInfo(MailAccount? account)
        => account is null
            ? null
            : MailAccountIconInfoFactory.Create(account, PictureStorageService);

    public static IAccountIconInfo GetAccountIconInfo(
        MailAccount? account,
        MailProviderType providerType,
        SpecialImapProvider specialImapProvider)
        => account is null
            ? MailAccountIconInfoFactory.CreateProviderFallback(providerType, specialImapProvider)
            : MailAccountIconInfoFactory.Create(account, PictureStorageService);

    public static IconElement GetAccountOrGlyphIcon(MailAccount? account, string glyph)
        => account is null
            ? new MailCal.Mail.WinUI.Controls.MailCalFontIcon { FontSize = 15, Glyph = glyph }
            : new MailCalAccountIcon
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
        // A sender that is one of the user's own accounts shows that account's picture
        // before the initials fallback.
        var localImagePath = (contact?.ContactPictureFileId is Guid fileId
                ? PictureStorageService.GetPicturePath(PictureKind.Contact, fileId)
                : null)
            ?? AccountSenderPictureDirectory.GetProfilePicturePath(resolvedAddress);

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

    public static Microsoft.UI.Xaml.Media.Imaging.BitmapImage? AccountAvatarToBitmapImage(string? path)
        => GetContactEditorPreviewPicture(null, path);

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

    /// <summary>
    /// Warning when the flag is set, informational otherwise. Used by hints that block a flow until
    /// the user changes something outside Mail-Cal.
    /// </summary>
    public static InfoBarSeverity BoolToInfoBarSeverity(bool isWarning)
        => isWarning ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;

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

    public static MailCalIconGlyph InfoBarMessageTypeGlyph(InfoBarMessageType messageType) => messageType switch
    {
        InfoBarMessageType.Success => MailCalIconGlyph.CheckmarkCircle,
        InfoBarMessageType.Warning => MailCalIconGlyph.Warning,
        InfoBarMessageType.Error => MailCalIconGlyph.DismissCircle,
        _ => MailCalIconGlyph.Info,
    };

    [DynamicWindowsRuntimeCast(typeof(Brush))]
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
    [DynamicWindowsRuntimeCast(typeof(Brush))]
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
        => new(MailCal.Mail.WinUI.MailCalApplication.Current.UnderlyingThemeService.IsUnderlyingThemeDark()
            ? Colors.White
            : Colors.Black);

    public static SolidColorBrush GetCategoryTextBrush(string textColorHex, string backgroundColorHex)
        => !string.IsNullOrWhiteSpace(textColorHex)
            ? GetSolidColorBrushFromHex(textColorHex)
            : string.IsNullOrWhiteSpace(backgroundColorHex)
                ? new SolidColorBrush(Colors.Black)
                : GetReadableTextColor(backgroundColorHex);
    public static FontWeight GetFontWeightBySyncState(bool isSyncing) => isSyncing ? FontWeights.SemiBold : FontWeights.Normal;

    [DynamicWindowsRuntimeCast(typeof(Brush))]
    public static Brush GetWizardStepBadgeBrush(bool isActive)
        => isActive
            ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
            : new SolidColorBrush(Color.FromArgb(30, 128, 128, 128));

    [DynamicWindowsRuntimeCast(typeof(Brush))]
    public static Brush GetWizardStepNumberForeground(bool isActive)
        => isActive
            ? new SolidColorBrush(Colors.White)
            : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

    [DynamicWindowsRuntimeCast(typeof(Brush))]
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

    public static HoverActionButtonSize GetHoverActionButtonSize(MailHoverActionButtonSize size) => size switch
    {
        MailHoverActionButtonSize.Medium => HoverActionButtonSize.Medium,
        MailHoverActionButtonSize.Large => HoverActionButtonSize.Large,
        _ => HoverActionButtonSize.Small,
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
        // The reader binds this before the MIME is parsed. An unset date must read as nothing,
        // not as "1 January 0001".
        if (date == default) return string.Empty;

        var localTime = date.ToLocalTime();
        var displayType = DateTimeDisplayFormatter.GetTimeDisplayType(timeFormatPreference, AppDisplayCulture);
        return $"{localTime.ToString("D", AppDisplayCulture)} {DateTimeDisplayFormatter.FormatTime(localTime, displayType, AppDisplayCulture)}";
    }
    public static string GetMailGroupDateString(object groupObject)
    {
        if (groupObject is global::MailCal.Mail.Controls.Core.MailListGroup mailListGroup)
        {
            groupObject = mailListGroup.Key;
        }

        if (groupObject is global::MailCal.Mail.Controls.Core.MailListProjectionGroupKey projectionGroupKey)
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

    public static string FormatAutomationName(string format, string? value)
        => string.Format(AppDisplayCulture, format, value ?? string.Empty);

    public static string GetItemActionAutomationName(string action, string? item)
        => string.IsNullOrWhiteSpace(item) ? action : string.Format(AppDisplayCulture, Translator.Accessibility_ItemAction, action, item);

    public static string GetAccountAutomationName(string? name, string? address)
        => JoinAutomationDetails(name, address);

    public static string GetContactAutomationName(string? name, string? address, string? source, bool isFavorite)
        => JoinAutomationDetails(name, address, source, isFavorite ? Translator.ContactEditor_Favorite : null);

    public static string GetShortcutAutomationName(string? action, string? keys, string? mode)
        => JoinAutomationDetails(action, keys, mode);

    private static string JoinAutomationDetails(params string?[] values)
        => string.Join(", ", values.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct());

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

    #region Mail-Cal Font Icon Transformation

    public static MailCalIconGlyph GetMailCalIconGlyph(FilterOptionType type) => type switch
    {
        FilterOptionType.All => MailCalIconGlyph.Mail,
        FilterOptionType.Unread => MailCalIconGlyph.MarkUnread,
        FilterOptionType.Flagged => MailCalIconGlyph.Flag,
        FilterOptionType.Mentions => MailCalIconGlyph.NewMail,
        FilterOptionType.Files => MailCalIconGlyph.Attachment,
        _ => MailCalIconGlyph.None,
    };

    public static MailCalIconGlyph GetMailCalIconGlyph(SortingOptionType type) => type switch
    {
        SortingOptionType.Sender => MailCalIconGlyph.SortTextDesc,
        SortingOptionType.ReceiveDate => MailCalIconGlyph.SortLinesDesc,
        _ => MailCalIconGlyph.None,
    };

    public static MailCalIconGlyph GetMailCalIconGlyph(MailOperation operation)
    {
        return operation switch
        {
            MailOperation.None => MailCalIconGlyph.None,
            MailOperation.Archive => MailCalIconGlyph.Archive,
            MailOperation.UnArchive => MailCalIconGlyph.UnArchive,
            MailOperation.SoftDelete => MailCalIconGlyph.Delete,
            MailOperation.HardDelete => MailCalIconGlyph.Delete,
            MailOperation.Move => MailCalIconGlyph.Forward,
            MailOperation.MoveToJunk => MailCalIconGlyph.Blocked,
            MailOperation.MoveToFocused => MailCalIconGlyph.None,
            MailOperation.MoveToOther => MailCalIconGlyph.None,
            MailOperation.AlwaysMoveToOther => MailCalIconGlyph.None,
            MailOperation.AlwaysMoveToFocused => MailCalIconGlyph.None,
            MailOperation.SetFlag => MailCalIconGlyph.Flag,
            MailOperation.ClearFlag => MailCalIconGlyph.ClearFlag,
            MailOperation.MarkAsRead => MailCalIconGlyph.MarkRead,
            MailOperation.MarkAsUnread => MailCalIconGlyph.MarkUnread,
            MailOperation.MarkAsNotJunk => MailCalIconGlyph.Blocked,
            MailOperation.Ignore => MailCalIconGlyph.Ignore,
            MailOperation.Reply => MailCalIconGlyph.Reply,
            MailOperation.ReplyAll => MailCalIconGlyph.ReplyAll,
            MailOperation.Zoom => MailCalIconGlyph.Zoom,
            MailOperation.SaveAs => MailCalIconGlyph.Save,
            MailOperation.SaveAsPdf => MailCalIconGlyph.Save,
            MailOperation.SaveAsEml => MailCalIconGlyph.ViewMessageSource,
            MailOperation.Print => MailCalIconGlyph.Print,
            MailOperation.Find => MailCalIconGlyph.Find,
            MailOperation.Forward => MailCalIconGlyph.Forward,
            MailOperation.DarkEditor => MailCalIconGlyph.DarkEditor,
            MailOperation.LightEditor => MailCalIconGlyph.LightEditor,
            MailOperation.ViewMessageSource => MailCalIconGlyph.ViewMessageSource,
            _ => MailCalIconGlyph.None,
        };
    }

    public static MailCalIconGlyph GetPathGeometry(FolderOperation operation)
    {
        return operation switch
        {
            FolderOperation.None => MailCalIconGlyph.None,
            FolderOperation.Pin => MailCalIconGlyph.Pin,
            FolderOperation.Unpin => MailCalIconGlyph.UnPin,
            FolderOperation.MarkAllAsRead => MailCalIconGlyph.MarkRead,
            FolderOperation.DontSync => MailCalIconGlyph.DontSync,
            FolderOperation.Empty => MailCalIconGlyph.EmptyFolder,
            FolderOperation.Rename => MailCalIconGlyph.Rename,
            FolderOperation.Delete => MailCalIconGlyph.Delete,
            FolderOperation.Move => MailCalIconGlyph.Forward,
            FolderOperation.TurnOffNotifications => MailCalIconGlyph.TurnOfNotifications,
            FolderOperation.CreateSubFolder => MailCalIconGlyph.CreateFolder,
            _ => MailCalIconGlyph.None,
        };
    }

    public static MailCalIconGlyph GetSpecialFolderPathIconGeometry(SpecialFolderType specialFolderType)
    {
        return specialFolderType switch
        {
            SpecialFolderType.Inbox => MailCalIconGlyph.SpecialFolderInbox,
            SpecialFolderType.Starred => MailCalIconGlyph.SpecialFolderStarred,
            SpecialFolderType.Important => MailCalIconGlyph.SpecialFolderImportant,
            SpecialFolderType.Sent => MailCalIconGlyph.SpecialFolderSent,
            SpecialFolderType.Draft => MailCalIconGlyph.SpecialFolderDraft,
            SpecialFolderType.Archive => MailCalIconGlyph.SpecialFolderArchive,
            SpecialFolderType.Deleted => MailCalIconGlyph.SpecialFolderDeleted,
            SpecialFolderType.Junk => MailCalIconGlyph.SpecialFolderJunk,
            SpecialFolderType.Chat => MailCalIconGlyph.SpecialFolderChat,
            SpecialFolderType.Category => MailCalIconGlyph.SpecialFolderCategory,
            SpecialFolderType.Unread => MailCalIconGlyph.SpecialFolderUnread,
            SpecialFolderType.Forums => MailCalIconGlyph.SpecialFolderForums,
            SpecialFolderType.Updates => MailCalIconGlyph.SpecialFolderUpdated,
            SpecialFolderType.Personal => MailCalIconGlyph.SpecialFolderPersonal,
            SpecialFolderType.Promotions => MailCalIconGlyph.SpecialFolderPromotions,
            SpecialFolderType.Social => MailCalIconGlyph.SpecialFolderSocial,
            SpecialFolderType.Other => MailCalIconGlyph.SpecialFolderOther,
            SpecialFolderType.More => MailCalIconGlyph.SpecialFolderMore,
            _ => MailCalIconGlyph.None,
        };
    }


    public static MailCalIconGlyph GetProviderIcon(MailProviderType providerType, SpecialImapProvider specialImapProvider)
    {
        if (specialImapProvider == SpecialImapProvider.None)
        {
            return providerType switch
            {
                MailProviderType.Outlook => MailCalIconGlyph.Microsoft,
                MailProviderType.Gmail => MailCalIconGlyph.Google,
                MailProviderType.IMAP4 => MailCalIconGlyph.IMAP,
                _ => MailCalIconGlyph.None,
            };
        }
        else
        {
            // Catalog providers have a brand glyph named after their enum member (Gmx, Proton, ...).
            return specialImapProvider switch
            {
                SpecialImapProvider.iCloud => MailCalIconGlyph.Apple,
                SpecialImapProvider.Yahoo => MailCalIconGlyph.Yahoo,
                _ => Enum.TryParse<MailCalIconGlyph>(specialImapProvider.ToString(), out var brandGlyph)
                    ? brandGlyph
                    : MailCalIconGlyph.IMAP,
            };
        }
    }
    public static MailCalIconGlyph GetProviderIcon(MailAccount account)
        => GetProviderIcon(account.ProviderType, account.SpecialImapProvider);

    /// <summary>
    /// Builds a Geometry from path markup held as a string. Named apart from the
    /// FolderOperation overload because x:Bind resolves helper functions by name alone and
    /// cannot choose between overloads.
    /// </summary>
    [DynamicWindowsRuntimeCast(typeof(Microsoft.UI.Xaml.Shapes.Path))]
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
