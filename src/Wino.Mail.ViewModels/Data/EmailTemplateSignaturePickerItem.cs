using System;
using Wino.Core.Domain.Entities.Mail;

namespace Wino.Mail.ViewModels.Data;

/// <summary>One row of the template editor's signature ComboBox (None or an account signature).</summary>
public sealed class EmailTemplateSignaturePickerItem
{
    public EmailTemplateSignaturePickerItem(string displayName, AccountSignature? signature = null)
    {
        DisplayName = displayName;
        Signature = signature;
        SignatureId = signature?.Id;
    }

    public string DisplayName { get; }

    public Guid? SignatureId { get; }

    public AccountSignature? Signature { get; }

    public bool IsNone => SignatureId == null;

    public override string ToString() => DisplayName;
}
