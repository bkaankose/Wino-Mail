using System;
using Wino.Core.Domain.Enums;

namespace Wino.Messaging.UI;

/// <summary>
/// A public folder was pinned or unpinned for an account. The kind names the surface that lists it:
/// the account's folders, People or Calendar.
/// </summary>
public record PublicFolderFavoritesChanged(Guid AccountId, PublicFolderKind Kind) : UIMessageBase<PublicFolderFavoritesChanged>;
