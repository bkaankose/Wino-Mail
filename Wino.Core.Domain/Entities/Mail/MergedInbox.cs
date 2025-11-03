using System;
using System.ComponentModel.DataAnnotations;

namespace Wino.Core.Domain.Entities.Mail;

public class MergedInbox
{
    [Key]
    public Guid Id { get; set; }

    public string Name { get; set; }
}
