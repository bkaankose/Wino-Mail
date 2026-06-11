using System.Threading.Tasks;
using MimeKit;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

/// <summary>
/// Companion-process-only surface of <see cref="IContactService"/> for extracting and saving
/// contacts straight from a parsed MimeMessage.
/// </summary>
public interface IContactServiceInternal : IContactService
{
    Task SaveAddressInformationAsync(MimeMessage message);
}
