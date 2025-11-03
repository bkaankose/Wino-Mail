using System;
using System.ComponentModel.DataAnnotations;

namespace Wino.Core.Domain.Entities.Shared;

public class Thumbnail
{
    [Key]
    public Guid Id { get; set; }

    public string Address { get; set; }
    public string Gravatar { get; set; }
    public string Favicon { get; set; }
    public DateTime LastUpdated { get; set; }
}
