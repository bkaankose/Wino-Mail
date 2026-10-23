// This file is used by Code Analysis to maintain SuppressMessage
// attributes that are applied to this project.
// Project-level suppressions either have no target or are given
// a specific target and scoped to a namespace, type, member, etc.

using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "<Pending>", Scope = "member", Target = "~P:MailCal.Core.Models.IMailDisplayInformation.asd")]
[assembly: SuppressMessage("Minor Code Smell", "S3267:Loops should be simplified with \"LINQ\" expressions", Justification = "<Pending>", Scope = "member", Target = "~M:MailCal.Core.Services.MailCalRequestProcessor.PrepareRequestsAsync(MailCal.Core.Domain.Enums.MailOperation,System.Collections.Generic.IEnumerable{System.String})~System.Threading.Tasks.Task{System.Collections.Generic.List{MailCal.Core.Abstractions.Interfaces.Data.IMailCalChangeRequest}}")]
[assembly: SuppressMessage("Minor Code Smell", "S3267:Loops should be simplified with \"LINQ\" expressions", Justification = "<Pending>", Scope = "member", Target = "~M:MailCal.Core.Services.SynchronizationWorker.QueueAsync(System.Collections.Generic.IEnumerable{MailCal.Core.Abstractions.Interfaces.Data.IMailCalChangeRequest})")]
