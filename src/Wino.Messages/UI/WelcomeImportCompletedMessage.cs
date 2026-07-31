namespace Wino.Messaging.UI;

public record WelcomeImportCompletedMessage(int ImportedMailboxCount) : UIMessageBase<WelcomeImportCompletedMessage>;
