namespace Wino.Mail.ViewModels.Data;

/// <summary>
/// Simple DTO for presenting contact information in the UI.
/// This is a presentation model, not a database entity.
/// </summary>
public class AccountContact
{
    public string Address { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Base64ContactPicture { get; set; }
    public bool IsRootContact { get; set; }
    public bool IsOverridden { get; set; }
}
