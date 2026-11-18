using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.Messaging.UI;
public record ThumbnailAdded(string Email): IUIMessage;
