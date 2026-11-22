using Wino.Mail.ViewModels;

namespace Wino.Mail.Linux.Views.Mail;

/// <summary>The invitation card above the body, bound to <c>ViewModel.Invitation</c> only.</summary>
internal sealed partial class MailReaderView
{
    private MailInvitationCard? _invitation;

    partial void BuildInvitation()
    {
        var card = _invitation = new MailInvitationCard(ViewModel.Invitation, Dispatcher, ReportError);
        _invitationSlot.SetChild(card.Root);
        Bindings.Own(card);
        // An empty visible slot would still add its bottom margin above the body.
        Bind.For(ViewModel.Invitation).Bind(nameof(MailInvitationCardViewModel.IsVisible), static vm => vm.IsVisible, _invitationSlot.SetVisible);
    }

    partial void InitializeInvitation()
    {
        if (_invitation is not { } card) return;
        WhenWidthChanged((_, state) => card.AnswersCompact = state != ReaderWidthState.Wide);
#if DEBUG
        WhenDescribing(card.Describe);
#endif
    }

#if DEBUG
    internal MailInvitationCard? DebugInvitation => _invitation;
#endif
}
