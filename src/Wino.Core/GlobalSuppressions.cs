// This file is used by Code Analysis to maintain SuppressMessage
// attributes that are applied to this project.
// Project-level suppressions either have no target or are given
// a specific target and scoped to a namespace, type, member, etc.

using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "<Pending>", Scope = "member", Target = "~P:Wino.Core.Models.IMailDisplayInformation.asd")]
[assembly: SuppressMessage("Minor Code Smell", "S3267:Loops should be simplified with \"LINQ\" expressions", Justification = "<Pending>", Scope = "member", Target = "~M:Wino.Core.Services.WinoRequestProcessor.PrepareRequestsAsync(Wino.Core.Domain.Enums.MailOperation,System.Collections.Generic.IEnumerable{System.String})~System.Threading.Tasks.Task{System.Collections.Generic.List{Wino.Core.Abstractions.Interfaces.Data.IWinoChangeRequest}}")]
[assembly: SuppressMessage("Minor Code Smell", "S3267:Loops should be simplified with \"LINQ\" expressions", Justification = "<Pending>", Scope = "member", Target = "~M:Wino.Core.Services.SynchronizationWorker.QueueAsync(System.Collections.Generic.IEnumerable{Wino.Core.Abstractions.Interfaces.Data.IWinoChangeRequest})")]
