namespace Wino.Messaging.UI;

public record WelcomeImportCompletedMessage(int ImportedMailboxCount, string CompletionMessage = "") : UIMessageBase<WelcomeImportCompletedMessage>;
