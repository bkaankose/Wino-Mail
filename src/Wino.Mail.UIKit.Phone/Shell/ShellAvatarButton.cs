using UIKit;
using Wino.Core.Domain;
using Wino.Mail.Controls.Core.AccountIcon;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Phone.Shell;

/// <summary>
/// The leading button of every iPhone tab's root bar (approach A of 18 Oct 2026, Outlook-like): the current account's
/// picture or provider logo in a 32pt circle; for a unified inbox two smaller avatars overlap. A tap opens the shell
/// drawer. The phone shell keeps every button it hands out in step with the account of the selected mail folder.
/// </summary>
internal sealed class ShellAvatarButton : UIButton
{
    private const double Single = 30;
    private const double Stacked = 22;

    private readonly WinoAccountIconView _front = new(Single) { UserInteractionEnabled = false };
    private readonly WinoAccountIconView _back = new(Stacked) { UserInteractionEnabled = false, Hidden = true };
    private readonly NSLayoutConstraint _frontCenterX;
    private readonly NSLayoutConstraint _frontCenterY;

    public ShellAvatarButton(Action open)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        AccessibilityLabel = Translator.IOS_Shell_OpenMenu;
        AddSubview(_back);
        AddSubview(_front);
        _frontCenterX = _front.CenterXAnchor.ConstraintEqualTo(CenterXAnchor);
        _frontCenterY = _front.CenterYAnchor.ConstraintEqualTo(CenterYAnchor);
        NSLayoutConstraint.ActivateConstraints(
        [
            WidthAnchor.ConstraintEqualTo(36),
            HeightAnchor.ConstraintEqualTo(36),
            _frontCenterX,
            _frontCenterY,
            _back.CenterXAnchor.ConstraintEqualTo(CenterXAnchor, -5),
            _back.CenterYAnchor.ConstraintEqualTo(CenterYAnchor, -5)
        ]);
        PrimaryActionTriggered += (_, _) => open();
    }

    /// <summary>Shows one account, or the first two of a unified inbox overlapping.</summary>
    public void SetAccounts(IReadOnlyList<IAccountIconInfo> accounts, string? name)
    {
        bool stacked = accounts.Count > 1;
        _front.Account = accounts.Count > 0 ? accounts[0] : null;
        _back.Account = stacked ? accounts[1] : null;
        _back.Hidden = !stacked;
        _front.Size = stacked ? Stacked : Single;
        _frontCenterX.Constant = stacked ? 5 : 0;
        _frontCenterY.Constant = stacked ? 5 : 0;
        // The front avatar of a pair gets a ring in the bar colour so the two read as separate circles.
        _front.Layer.BorderWidth = stacked ? 1.5f : 0;
        _front.Layer.BorderColor = UIColor.SystemBackground.CGColor;
        _front.Layer.CornerRadius = (nfloat)(_front.Size / 2);
        AccessibilityValue = name;
    }
}
