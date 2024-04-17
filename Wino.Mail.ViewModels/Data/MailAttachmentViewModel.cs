using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using MimeKit;
using Wino.Core.Domain.Enums;
using Wino.Core.Extensions;

namespace Wino.Mail.ViewModels.Data
{
    public class MailAttachmentViewModel : ObservableObject
    {
        private bool isBusy;
        private readonly MimePart _mimePart;

        public MailAttachmentType AttachmentType { get; }
        public string FileName { get; }
        public string FilePath { get; set; }
        public string ReadableSize { get; }
        public byte[] Content { get; set; }

        public IMimeContent MimeContent => _mimePart.Content;

        /// <summary>
        /// Gets or sets whether attachment is busy with opening or saving etc.
        /// </summary>
        public bool IsBusy
        {
            get => isBusy;
            set => SetProperty(ref isBusy, value);
        }

        public MailAttachmentViewModel(MimePart mimePart)
        {
            _mimePart = mimePart;

            var array = new byte[_mimePart.Content.Stream.Length];
            _mimePart.Content.Stream.Read(array, 0, (int)_mimePart.Content.Stream.Length);

            Content = array;

            FileName = mimePart.FileName;
            ReadableSize = mimePart.Content.Stream.Length.GetBytesReadable();

            var extension = Path.GetExtension(FileName);
            AttachmentType = GetAttachmentType(extension);
        }

        public MailAttachmentViewModel(string fullFilePath, byte[] content)
        {
            Content = content;

            FileName = Path.GetFileName(fullFilePath);
            FilePath = fullFilePath;

            ReadableSize = ((long)content.Length).GetBytesReadable();

            var extension = Path.GetExtension(FileName);
            AttachmentType = GetAttachmentType(extension);
        }

        public MailAttachmentType GetAttachmentType(string mediaSubtype)
        {
            if (string.IsNullOrEmpty(mediaSubtype))
                return MailAttachmentType.None;

            switch (mediaSubtype.ToLower())
            {
                case ".exe":
                    return MailAttachmentType.Executable;
                case ".rar":
                    return MailAttachmentType.RarArchive;
                case ".zip":
                    return MailAttachmentType.Archive;
                case ".ogg":
                case ".mp3":
                case ".wav":
                case ".aac":
                case ".alac":
                    return MailAttachmentType.Audio;
                case ".mp4":
                case ".wmv":
                case ".avi":
                case ".flv":
                    return MailAttachmentType.Video;
                case ".pdf":
                    return MailAttachmentType.PDF;
                case ".htm":
                case ".html":
                    return MailAttachmentType.HTML;
                case ".png":
                case ".jpg":
                case ".jpeg":
                case ".gif":
                case ".jiff":
                    return MailAttachmentType.Image;
                default:
                    return MailAttachmentType.Other;
            }
        }
    }
}
