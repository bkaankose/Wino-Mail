namespace MailCal.Core.Domain.Interfaces;

public interface IAppMetadataService
{
    string AppVersion { get; }
    string PackageName { get; }
    string BuildConfiguration { get; }
}
