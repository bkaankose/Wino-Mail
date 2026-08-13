using MimeKit;

namespace Wino.Core.Domain.Models.MailItem;

/// <summary>
/// Encapsulates MimeMessage and the path to the file.
/// </summary>
public record MimeMessageInformation(MimeMessage MimeMessage, string Path);
