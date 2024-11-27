using System.Text.Json.Serialization.Metadata;
using MailKit;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Requests.Folder;
using Wino.Core.Requests.Mail;

namespace Wino.Core.Integration.Json
{
    public class ServerRequestTypeInfoResolver : DefaultJsonTypeInfoResolver
    {
        public ServerRequestTypeInfoResolver()
        {
            Modifiers.Add(new System.Action<JsonTypeInfo>(t =>
            {
                if (t.Type == typeof(IRequestBase))
                {
                    t.PolymorphismOptions = new()
                    {
                        DerivedTypes =
                        {
                            new JsonDerivedType(typeof(AlwaysMoveToRequest), nameof(AlwaysMoveToRequest)),
                            new JsonDerivedType(typeof(ArchiveRequest), nameof(ArchiveRequest)),
                            new JsonDerivedType(typeof(ChangeFlagRequest), nameof(ChangeFlagRequest)),
                            new JsonDerivedType(typeof(CreateDraftRequest), nameof(CreateDraftRequest)),
                            new JsonDerivedType(typeof(DeleteRequest), nameof(DeleteRequest)),
                            new JsonDerivedType(typeof(EmptyFolderRequest), nameof(EmptyFolderRequest)),
                            new JsonDerivedType(typeof(MarkFolderAsReadRequest), nameof(MarkFolderAsReadRequest)),
                            new JsonDerivedType(typeof(MarkReadRequest), nameof(MarkReadRequest)),
                            new JsonDerivedType(typeof(MoveRequest), nameof(MoveRequest)),
                            new JsonDerivedType(typeof(MoveToFocusedRequest), nameof(MoveToFocusedRequest)),
                            new JsonDerivedType(typeof(RenameFolderRequest), nameof(RenameFolderRequest)),
                            new JsonDerivedType(typeof(SendDraftRequest), nameof(SendDraftRequest)),
                        }
                    };
                }
                else if (t.Type == typeof(IMailItem))
                {
                    t.PolymorphismOptions = new JsonPolymorphismOptions()
                    {
                        DerivedTypes =
                        {
                            new JsonDerivedType(typeof(MailCopy), nameof(MailCopy)),
                        }
                    };
                }
                else if (t.Type == typeof(IMailFolder))
                {
                    t.PolymorphismOptions = new JsonPolymorphismOptions()
                    {
                        DerivedTypes =
                        {
                            new JsonDerivedType(typeof(MailItemFolder), nameof(MailItemFolder)),
                        }
                    };
                }
            }));
        }
    }
}
