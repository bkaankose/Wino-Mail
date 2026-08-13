namespace Wino.Core.Domain.Models.Thumbnails;

public enum ThumbnailKind
{
    Gravatar,
    Favicon
}

public sealed record ThumbnailResult(string FilePath, string AppDataUri, ThumbnailKind Kind);
