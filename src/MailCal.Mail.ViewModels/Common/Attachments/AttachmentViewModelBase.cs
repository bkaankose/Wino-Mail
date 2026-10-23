#nullable enable
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using MailCal.Core.Domain;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Helpers;
using MailCal.Core.Domain.Models.Attachments;

namespace MailCal.Core.ViewModels.Attachments;

public abstract partial class AttachmentViewModelBase : ObservableObject
{
    public abstract string FileName { get; }

    public MailAttachmentType AttachmentType => AttachmentFileTypeHelper.GetAttachmentType(FileName);

    public bool HasContentTypeMismatch => ContentTypeDetection.RequiresOpenConfirmation;

    public string ContentTypeWarningText => HasContentTypeMismatch
        ? string.Format(
            Translator.Attachment_ContentTypeMismatchInline,
            ContentTypeDetection.Description ?? ContentTypeDetection.Label ?? Translator.Attachment_UnknownContentType)
        : string.Empty;

    public Task<ContentTypeDetectionResult>? InspectionTask { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasContentTypeMismatch))]
    [NotifyPropertyChangedFor(nameof(ContentTypeWarningText))]
    public partial ContentTypeDetectionResult ContentTypeDetection { get; set; } = ContentTypeDetectionResult.NotInspected;

    public void BeginInspection(Task<ContentTypeDetectionResult> inspectionTask)
    {
        InspectionTask = inspectionTask;
    }

    public Task<ContentTypeDetectionResult> GetInspectionAsync() =>
        InspectionTask ?? Task.FromResult(ContentTypeDetection);
}
