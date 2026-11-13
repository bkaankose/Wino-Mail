#nullable enable
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Helpers;
using Wino.Core.Domain.Models.Attachments;

namespace Wino.Core.ViewModels.Attachments;

public abstract partial class AttachmentViewModelBase : ObservableObject
{
    public abstract string FileName { get; }

    public abstract string ReadableSize { get; }

    public MailAttachmentType AttachmentType => AttachmentFileTypeHelper.GetAttachmentType(FileName);

    public string FileExtensionLabel => AttachmentFileTypeHelper.GetExtensionLabel(FileName);

    public uint AccentColor => AttachmentFileTypeHelper.GetAccentColor(FileName);

    public string DetailsText => string.IsNullOrEmpty(FileExtensionLabel)
        ? ReadableSize
        : $"{ReadableSize} · {FileExtensionLabel}";

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
