using System;
using System.Collections.Generic;
using System.Linq;
using Windows.UI.Xaml.Controls;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Models.Folders;

namespace Wino.Dialogs
{
    public sealed partial class SystemFolderConfigurationDialog : ContentDialog
    {
        private bool canDismissDialog = false;

        public SystemFolderConfiguration Configuration { get; set; }
        public List<MailItemFolder> AvailableFolders { get; }

        public MailItemFolder Sent { get; set; }
        public MailItemFolder Draft { get; set; }
        public MailItemFolder Archive { get; set; }
        public MailItemFolder Junk { get; set; }
        public MailItemFolder Trash { get; set; }

        public SystemFolderConfigurationDialog(List<MailItemFolder> availableFolders)
        {
            InitializeComponent();

            AvailableFolders = availableFolders;

            Sent = AvailableFolders.Find(a => a.SpecialFolderType == Core.Domain.Enums.SpecialFolderType.Sent);
            Draft = AvailableFolders.Find(a => a.SpecialFolderType == Core.Domain.Enums.SpecialFolderType.Draft);
            Archive = AvailableFolders.Find(a => a.SpecialFolderType == Core.Domain.Enums.SpecialFolderType.Archive);
            Junk = AvailableFolders.Find(a => a.SpecialFolderType == Core.Domain.Enums.SpecialFolderType.Junk);
            Trash = AvailableFolders.Find(a => a.SpecialFolderType == Core.Domain.Enums.SpecialFolderType.Deleted);
        }

        private void DialogClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
        {
            args.Cancel = !canDismissDialog;
        }

        private void CancelClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
            => canDismissDialog = true;

        private void SaveClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            ValidationErrorTextBlock.Text = string.Empty;

            var allSpecialFolders = new List<MailItemFolder>()
            {
                Sent, Draft, Archive, Trash, Junk
            };

            if (allSpecialFolders.Any(a => a != null && a.SpecialFolderType == SpecialFolderType.Inbox))
                ValidationErrorTextBlock.Text = Translator.SystemFolderConfigDialogValidation_InboxSelected;

            if (new HashSet<Guid>(allSpecialFolders.Where(a => a != null).Select(x => x.Id)).Count != allSpecialFolders.Where(a => a != null).Count())
                ValidationErrorTextBlock.Text = Translator.SystemFolderConfigDialogValidation_DuplicateSystemFolders;

            // Check if we can save.
            if (string.IsNullOrEmpty(ValidationErrorTextBlock.Text))
            {
                var configuration = new SystemFolderConfiguration(Sent, Draft, Archive, Trash, Junk);

                canDismissDialog = true;
                Configuration = configuration;
            }
        }
    }
}
