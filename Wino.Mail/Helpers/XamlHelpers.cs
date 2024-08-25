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
        public static bool ConnectionStatusEquals(WinoServerConnectionStatus winoServerConnectionStatus, WinoServerConnectionStatus connectionStatus) => winoServerConnectionStatus == connectionStatus;

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
                MailOperation.MoveToJunk => Translator.MailOperation_MoveJunk,
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
                MailOperation.Find => Translator.MailOperation_Find,
                MailOperation.Forward => Translator.MailOperation_Forward,
                MailOperation.DarkEditor => string.Empty,
                MailOperation.LightEditor => string.Empty,
                MailOperation.Print => Translator.MailOperation_Print,
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
}
