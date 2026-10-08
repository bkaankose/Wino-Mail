namespace Wino.Core.Domain.Enums;

public enum ImapAuthenticationMethod
{
    Auto,
    None,
    NormalPassword,
    EncryptedPassword,
    Ntlm,
    CramMd5,
    DigestMd5
}
