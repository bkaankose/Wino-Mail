using System;
using System.Linq;
using Microsoft.Toolkit.Uwp.Helpers;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Markup;
using Windows.UI.Xaml.Media;
using Wino.Controls;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Reader;

namespace Wino.Helpers
{
    public static class XamlHelpers
    {
        private const string TwentyFourHourTimeFormat = "HH:mm";
        private const string TwelveHourTimeFormat = "hh:mm tt";
        #region Converters

        public static Visibility ReverseBoolToVisibilityConverter(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
        public static Visibility ReverseVisibilityConverter(Visibility visibility) => visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        public static bool ReverseBoolConverter(bool value) => !value;
        public static bool ShouldDisplayPreview(string text) => text.Any(x => char.IsLetter(x));
        public static bool CountToBooleanConverter(int value) => value > 0;
        public static bool ObjectEquals(object obj1, object obj2) => object.Equals(obj1, obj2);
        public static Visibility CountToVisibilityConverter(int value) => value > 0 ? Visibility.Visible : Visibility.Collapsed;
        public static InfoBarSeverity InfoBarSeverityConverter(InfoBarMessageType messageType)
        {
            switch (messageType)
            {
                case InfoBarMessageType.Information:
                    return InfoBarSeverity.Informational;
                case InfoBarMessageType.Success:
                    return InfoBarSeverity.Success;
                case InfoBarMessageType.Warning:
                    return InfoBarSeverity.Warning;
                case InfoBarMessageType.Error:
                    return InfoBarSeverity.Error;
                default:
                    return InfoBarSeverity.Informational;
            }
        }
        public static SolidColorBrush GetSolidColorBrushFromHex(string colorHex) => string.IsNullOrEmpty(colorHex) ? new SolidColorBrush(Colors.Transparent) : new SolidColorBrush(colorHex.ToColor());
        public static Visibility IsSelectionModeMultiple(ListViewSelectionMode mode) => mode == ListViewSelectionMode.Multiple ? Visibility.Visible : Visibility.Collapsed;
        public static FontWeight GetFontWeightBySyncState(bool isSyncing) => isSyncing ? FontWeights.SemiBold : FontWeights.Normal;
        public static FontWeight GetFontWeightByChildSelectedState(bool isChildSelected) => isChildSelected ? FontWeights.SemiBold : FontWeights.Normal;
        public static Geometry GetPathIcon(string resourceName) => GetPathGeometry(Application.Current.Resources[$"{resourceName}"] as string);
        public static GridLength GetGridLength(double width) => new GridLength(width, GridUnitType.Pixel);
        public static double MailListAdaptivityConverter(double mailListPaneLength) => mailListPaneLength + 300d;
        public static string GetMailItemDisplaySummaryForListing(bool isDraft, DateTime receivedDate, bool prefer24HourTime)
        {
            if (isDraft)
                return Translator.Draft;
            else
            {
                var localTime = receivedDate.ToLocalTime();

                return prefer24HourTime ? localTime.ToString(TwentyFourHourTimeFormat) : localTime.ToString(TwelveHourTimeFormat);
            }
        }

        public static string GetCreationDateString(DateTime date, bool prefer24HourTime)
        {
            var localTime = date.ToLocalTime();
            return $"{localTime.ToLongDateString()} {(prefer24HourTime ? localTime.ToString(TwentyFourHourTimeFormat) : localTime.ToString(TwelveHourTimeFormat))}";
        }

        public static string GetMailGroupDateString(object groupObject)
        {
            if (groupObject is string stringObject)
                return stringObject;

            object dateObject = null;

            // From regular mail header template
            if (groupObject is DateTime groupedDate)
                dateObject = groupedDate;
            else if (groupObject is IGrouping<object, IMailItem> groupKey)
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
                        return dateTimeValue.ToLongDateString();
                }
                else
                    return dateObject.ToString();
            }

            return Translator.UnknownDateHeader;
        }

        #endregion

        #region Wino Font Icon Transformation

        public static WinoIconGlyph GetWinoIconGlyph(FilterOptionType type) => type switch
        {
            FilterOptionType.All => WinoIconGlyph.SpecialFolderCategory,
            FilterOptionType.Unread => WinoIconGlyph.MarkUnread,
            FilterOptionType.Flagged => WinoIconGlyph.Flag,
            FilterOptionType.Mentions => WinoIconGlyph.NewMail,
            // TODO: Attachments icon should be added to WinoIcons.ttf.
            FilterOptionType.Files => WinoIconGlyph.None,
            _ => WinoIconGlyph.None,
        };

        public static WinoIconGlyph GetWinoIconGlyph(MailOperation operation)
        {
            switch (operation)
            {
                case MailOperation.None:
                    return WinoIconGlyph.None;
                case MailOperation.Archive:
                    return WinoIconGlyph.Archive;
                case MailOperation.UnArchive:
                    return WinoIconGlyph.UnArchive;
                case MailOperation.SoftDelete:
                case MailOperation.HardDelete:
                    return WinoIconGlyph.Delete;
                case MailOperation.Move:
                    return WinoIconGlyph.Forward;
                case MailOperation.MoveToJunk:
                    return WinoIconGlyph.Junk;
                case MailOperation.MoveToFocused:
                    break;
                case MailOperation.MoveToOther:
                    break;
                case MailOperation.AlwaysMoveToOther:
                    break;
                case MailOperation.AlwaysMoveToFocused:
                    break;
                case MailOperation.SetFlag:
                    return WinoIconGlyph.Flag;
                case MailOperation.ClearFlag:
                    return WinoIconGlyph.ClearFlag;
                case MailOperation.MarkAsRead:
                    return WinoIconGlyph.MarkRead;
                case MailOperation.MarkAsUnread:
                    return WinoIconGlyph.MarkUnread;
                case MailOperation.MarkAsNotJunk:
                    return WinoIconGlyph.Junk;
                case MailOperation.Ignore:
                    return WinoIconGlyph.Ignore;
                case MailOperation.Reply:
                    return WinoIconGlyph.Reply;
                case MailOperation.ReplyAll:
                    return WinoIconGlyph.ReplyAll;
                case MailOperation.Zoom:
                    return WinoIconGlyph.Zoom;
                case MailOperation.SaveAs:
                    return WinoIconGlyph.Save;
                case MailOperation.Find:
                    return WinoIconGlyph.Find;
                case MailOperation.Forward:
                    return WinoIconGlyph.Forward;
                case MailOperation.DarkEditor:
                    return WinoIconGlyph.DarkEditor;
                case MailOperation.LightEditor:
                    return WinoIconGlyph.LightEditor;
            }

            return WinoIconGlyph.None;
        }

        public static WinoIconGlyph GetPathGeometry(FolderOperation operation)
        {
            switch (operation)
            {
                case FolderOperation.None:
                    return WinoIconGlyph.None;
                case FolderOperation.Pin:
                    return WinoIconGlyph.Pin;
                case FolderOperation.Unpin:
                    return WinoIconGlyph.UnPin;
                case FolderOperation.MarkAllAsRead:
                    return WinoIconGlyph.MarkRead;
                case FolderOperation.DontSync:
                    return WinoIconGlyph.DontSync;
                case FolderOperation.Empty:
                    return WinoIconGlyph.EmptyFolder;
                case FolderOperation.Rename:
                    return WinoIconGlyph.Rename;
                case FolderOperation.Delete:
                    return WinoIconGlyph.Delete;
                case FolderOperation.Move:
                    return WinoIconGlyph.Forward;
                case FolderOperation.TurnOffNotifications:
                    return WinoIconGlyph.TurnOfNotifications;
                case FolderOperation.CreateSubFolder:
                    return WinoIconGlyph.CreateFolder;
            }

            return WinoIconGlyph.None;
        }

        public static WinoIconGlyph GetSpecialFolderPathIconGeometry(SpecialFolderType specialFolderType)
        {
            switch (specialFolderType)
            {
                case SpecialFolderType.Inbox:
                    return WinoIconGlyph.SpecialFolderInbox;
                case SpecialFolderType.Starred:
                    return WinoIconGlyph.SpecialFolderStarred;
                case SpecialFolderType.Important:
                    return WinoIconGlyph.SpecialFolderImportant;
                case SpecialFolderType.Sent:
                    return WinoIconGlyph.SpecialFolderSent;
                case SpecialFolderType.Draft:
                    return WinoIconGlyph.SpecialFolderDraft;
                case SpecialFolderType.Archive:
                    return WinoIconGlyph.SpecialFolderArchive;
                case SpecialFolderType.Deleted:
                    return WinoIconGlyph.SpecialFolderDeleted;
                case SpecialFolderType.Junk:
                    return WinoIconGlyph.SpecialFolderJunk;
                case SpecialFolderType.Chat:
                    return WinoIconGlyph.SpecialFolderChat;
                case SpecialFolderType.Category:
                    return WinoIconGlyph.SpecialFolderCategory;
                case SpecialFolderType.Unread:
                    return WinoIconGlyph.SpecialFolderUnread;
                case SpecialFolderType.Forums:
                    return WinoIconGlyph.SpecialFolderForums;
                case SpecialFolderType.Updates:
                    return WinoIconGlyph.SpecialFolderUpdated;
                case SpecialFolderType.Personal:
                    return WinoIconGlyph.SpecialFolderPersonal;
                case SpecialFolderType.Promotions:
                    return WinoIconGlyph.SpecialFolderPromotions;
                case SpecialFolderType.Social:
                    return WinoIconGlyph.SpecialFolderSocial;
                case SpecialFolderType.Other:
                    return WinoIconGlyph.SpecialFolderOther;
                case SpecialFolderType.More:
                    return WinoIconGlyph.SpecialFolderMore;
            }

            return WinoIconGlyph.None;
        }

        public static WinoIconGlyph GetProviderIcon(MailProviderType providerType)
        {
            switch (providerType)
            {
                case MailProviderType.Outlook:
                    return WinoIconGlyph.Microsoft;
                case MailProviderType.Gmail:
                    return WinoIconGlyph.Google;
                case MailProviderType.Office365:
                    return WinoIconGlyph.Microsoft;
                case MailProviderType.IMAP4:
                    return WinoIconGlyph.Mail;
            }

            return WinoIconGlyph.None;
        }

        public static Geometry GetPathGeometry(string pathMarkup)
        {
            string xaml =
            "<Path " +
            "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
            "<Path.Data>" + pathMarkup + "</Path.Data></Path>";
            var path = XamlReader.Load(xaml) as Windows.UI.Xaml.Shapes.Path;

            Geometry geometry = path.Data;
            path.Data = null;
            return geometry;
        }

        #endregion

        #region Toolbar Section Initialization

        public static Visibility IsFormatSection(EditorToolbarSection section) => section?.SectionType == EditorToolbarSectionType.Format ? Visibility.Visible : Visibility.Collapsed;
        public static Visibility IsInsertSection(EditorToolbarSection section) => section?.SectionType == EditorToolbarSectionType.Insert ? Visibility.Visible : Visibility.Collapsed;
        public static Visibility IsDrawSection(EditorToolbarSection section) => section?.SectionType == EditorToolbarSectionType.Draw ? Visibility.Visible : Visibility.Collapsed;
        public static Visibility IsOptionsSection(EditorToolbarSection section) => section?.SectionType == EditorToolbarSectionType.Options ? Visibility.Visible : Visibility.Collapsed;

        #endregion

        #region Internationalization

        public static string GetOperationString(MailOperation operation)
        {
            switch (operation)
            {
                case MailOperation.None:
                    return "unknown";
                case MailOperation.Archive:
                    return Translator.MailOperation_Archive;
                case MailOperation.UnArchive:
                    return Translator.MailOperation_Unarchive;
                case MailOperation.SoftDelete:
                    return Translator.MailOperation_Delete;
                case MailOperation.HardDelete:
                    return Translator.MailOperation_Delete;
                case MailOperation.Move:
                    return Translator.MailOperation_Move;
                case MailOperation.MoveToJunk:
                    return Translator.MailOperation_MoveJunk;
                case MailOperation.MoveToFocused:
                    return Translator.MailOperation_MoveFocused;
                case MailOperation.MoveToOther:
                    return Translator.MailOperation_MoveOther;
                case MailOperation.AlwaysMoveToOther:
                    return Translator.MailOperation_AlwaysMoveOther;
                case MailOperation.AlwaysMoveToFocused:
                    return Translator.MailOperation_AlwaysMoveFocused;
                case MailOperation.SetFlag:
                    return Translator.MailOperation_SetFlag;
                case MailOperation.ClearFlag:
                    return Translator.MailOperation_ClearFlag;
                case MailOperation.MarkAsRead:
                    return Translator.MailOperation_MarkAsRead;
                case MailOperation.MarkAsUnread:
                    return Translator.MailOperation_MarkAsUnread;
                case MailOperation.MarkAsNotJunk:
                    return Translator.MailOperation_MarkNotJunk;
                case MailOperation.Seperator:
                    return string.Empty;
                case MailOperation.Ignore:
                    return Translator.MailOperation_Ignore;
                case MailOperation.Reply:
                    return Translator.MailOperation_Reply;
                case MailOperation.ReplyAll:
                    return Translator.MailOperation_ReplyAll;
                case MailOperation.Zoom:
                    return Translator.MailOperation_Zoom;
                case MailOperation.SaveAs:
                    return Translator.MailOperation_SaveAs;
                case MailOperation.Find:
                    return Translator.MailOperation_Find;
                case MailOperation.Forward:
                    return Translator.MailOperation_Forward;
                case MailOperation.DarkEditor:
                    return string.Empty;
                case MailOperation.LightEditor:
                    return string.Empty;
                case MailOperation.Print:
                    return Translator.MailOperation_Print;
                case MailOperation.Navigate:
                    return Translator.MailOperation_Navigate;
                default:
                    return "unknown";
            }
        }

        public static string GetOperationString(FolderOperation operation)
        {
            switch (operation)
            {
                case FolderOperation.None:
                    break;
                case FolderOperation.Pin:
                    return Translator.FolderOperation_Pin;
                case FolderOperation.Unpin:
                    return Translator.FolderOperation_Unpin;
                case FolderOperation.MarkAllAsRead:
                    return Translator.FolderOperation_MarkAllAsRead;
                case FolderOperation.DontSync:
                    return Translator.FolderOperation_DontSync;
                case FolderOperation.Empty:
                    return Translator.FolderOperation_Empty;
                case FolderOperation.Rename:
                    return Translator.FolderOperation_Rename;
                case FolderOperation.Delete:
                    return Translator.FolderOperation_Delete;
                case FolderOperation.Move:
                    return Translator.FolderOperation_Move;
                case FolderOperation.CreateSubFolder:
                    return Translator.FolderOperation_CreateSubFolder;
            }

            return string.Empty;
        }

        #endregion
    }
}
