namespace Wino.Messaging.Enums;

public enum MessageType
{
    UIMessage, // For database changes that needs to be reflected in the UI. Either client sends it to itself or server sends it to client.
    ServerMessage, // For all actions that UWP awaits a response from Server. Caller is awaited, response returned in the app service connection args.
}
