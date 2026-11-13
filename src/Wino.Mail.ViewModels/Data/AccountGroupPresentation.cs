using Wino.Core.Domain;

namespace Wino.Mail.ViewModels.Data;

public static class AccountGroupPresentation
{
    /// <summary>"Account group · N accounts", the description of a group row.</summary>
    public static string GetAccountCountText(int count)
        => count == 1
            ? Translator.AccountGroup_AccountCountSingular
            : string.Format(Translator.AccountGroup_AccountCountPlural, count);
}
