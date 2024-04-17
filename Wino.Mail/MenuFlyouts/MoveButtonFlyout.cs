using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Core.Domain.Entities;

namespace Wino.MenuFlyouts
{
    public class MoveButtonMenuItemClickedEventArgs
    {
        public Guid ClickedFolderId { get; set; }
    }

    public class MoveButtonFlyout : MenuFlyout
    {
        public event TypedEventHandler<MoveButtonFlyout, MoveButtonMenuItemClickedEventArgs> MenuItemClick;
        public static readonly DependencyProperty FoldersProperty = DependencyProperty.Register(nameof(Folders), typeof(List<MailItemFolder>), typeof(MoveButtonFlyout), new PropertyMetadata(null, new PropertyChangedCallback(OnFoldersChanged)));

        public List<MailItemFolder> Folders
        {
            get { return (List<MailItemFolder>)GetValue(FoldersProperty); }
            set { SetValue(FoldersProperty, value); }
        }

        private static void OnFoldersChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
        {
            if (obj is MoveButtonFlyout menu)
            {
                menu.InitializeMenu();
            }


        }

        private void InitializeMenu()
        {
            Dispose();

            Items.Clear();

            if (Folders == null || !Folders.Any())
                return;

            // TODO: Child folders.

            foreach (var item in Folders)
            {
                // We don't expect this, but it crashes startup.
                // Just to be on the safe side.
                if (item.FolderName != null)
                {
                    var folderMenuItem = new MenuFlyoutItem()
                    {
                        Tag = item,
                        Text = item.FolderName
                    };

                    folderMenuItem.Click += MenuItemClicked;

                    Items.Add(folderMenuItem);
                }
            }
        }

        private void MenuItemClicked(object sender, RoutedEventArgs e)
        {
            var clickedFolder = (sender as MenuFlyoutItem).Tag as MailItemFolder;

            MenuItemClick?.Invoke(this, new MoveButtonMenuItemClickedEventArgs()
            {
                ClickedFolderId = clickedFolder.Id
            });
        }

        public void Dispose()
        {
            foreach (var item in Items)
            {
                if (item is MenuFlyoutItem menuItem)
                {
                    menuItem.Click -= MenuItemClicked;
                }
            }
        }
    }
}
