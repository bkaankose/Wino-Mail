using Wino.Core.Domain.Interfaces;

namespace Wino.Messaging.UI;
public record ThumbnailAdded(string Email): IUIMessage;
