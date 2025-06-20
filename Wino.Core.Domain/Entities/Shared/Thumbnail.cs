using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Shared;

public class Thumbnail
{
    [PrimaryKey]
    public string Domain { get; set; }

    public string Gravatar { get; set; }
    public string Favicon { get; set; }
    public DateTime LastUpdated { get; set; }
}
