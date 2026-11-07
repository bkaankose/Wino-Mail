#nullable enable
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Voice;
using Wino.Mail.Api.Contracts.Voice;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Matches each command of a voice plan to the local account it names and writes the step text, so the voice UI can
/// show what will happen in which account before anything runs.
/// </summary>
public interface IVoicePlanPreviewService
{
    Task<VoicePlanPreview> CreateAsync(VoicePlan plan);
}
