using System.Linq;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Requests;

public static class RequestEntityCloner
{
    public static AccountContact Contact(AccountContact source)
    {
        if (source is null)
            return null;

        return new AccountContact
        {
            Id = source.Id,
            MailAccountId = source.MailAccountId,
            AddressBookId = source.AddressBookId,
            SourceKind = source.SourceKind,
            RemoteId = source.RemoteId,
            RemoteVersion = source.RemoteVersion,
            RemotePhotoKey = source.RemotePhotoKey,
            DisplayName = source.DisplayName,
            HonorificPrefix = source.HonorificPrefix,
            GivenName = source.GivenName,
            MiddleName = source.MiddleName,
            Surname = source.Surname,
            HonorificSuffix = source.HonorificSuffix,
            Nickname = source.Nickname,
            FileAs = source.FileAs,
            CompanyName = source.CompanyName,
            Department = source.Department,
            JobTitle = source.JobTitle,
            OfficeLocation = source.OfficeLocation,
            Profession = source.Profession,
            BirthdayYear = source.BirthdayYear,
            BirthdayMonth = source.BirthdayMonth,
            BirthdayDay = source.BirthdayDay,
            Notes = source.Notes,
            Website = source.Website,
            ContactPictureFileId = source.ContactPictureFileId,
            IsAutoCollected = source.IsAutoCollected,
            IsFavorite = source.IsFavorite,
            PendingMutation = source.PendingMutation,
            CreatedAtUtc = source.CreatedAtUtc,
            ModifiedAtUtc = source.ModifiedAtUtc,
            SortKey = source.SortKey,
            IsRootContact = source.IsRootContact,
            IsOverridden = source.IsOverridden,
            EmailAddresses = source.EmailAddresses?.Select(item => new ContactEmailAddress
            {
                Id = item.Id,
                ContactId = item.ContactId,
                Address = item.Address,
                NormalizedAddress = item.NormalizedAddress,
                Label = item.Label,
                Order = item.Order,
                IsPrimary = item.IsPrimary
            }).ToList() ?? [],
            PhoneNumbers = source.PhoneNumbers?.Select(item => new ContactPhoneNumber
            {
                Id = item.Id,
                ContactId = item.ContactId,
                Number = item.Number,
                Kind = item.Kind,
                Order = item.Order,
                IsPrimary = item.IsPrimary
            }).ToList() ?? [],
            PostalAddresses = source.PostalAddresses?.Select(item => new ContactPostalAddress
            {
                Id = item.Id,
                ContactId = item.ContactId,
                Kind = item.Kind,
                PostOfficeBox = item.PostOfficeBox,
                Street = item.Street,
                City = item.City,
                Region = item.Region,
                PostalCode = item.PostalCode,
                Country = item.Country
            }).ToList() ?? [],
            ImAddresses = source.ImAddresses?.Select(item => new ContactImAddress
            {
                Id = item.Id,
                ContactId = item.ContactId,
                Address = item.Address,
                Protocol = item.Protocol,
                Order = item.Order
            }).ToList() ?? [],
            Relations = source.Relations?.Select(item => new ContactRelation
            {
                Id = item.Id,
                ContactId = item.ContactId,
                Kind = item.Kind,
                Name = item.Name,
                Order = item.Order
            }).ToList() ?? []
        };
    }

    public static AccountTaskList TaskList(AccountTaskList source)
        => source is null
            ? null
            : new AccountTaskList
            {
                Id = source.Id,
                MailAccountId = source.MailAccountId,
                SourceKind = source.SourceKind,
                RemoteId = source.RemoteId,
                RemoteVersion = source.RemoteVersion,
                ListDeltaLink = source.ListDeltaLink,
                TaskDeltaLink = source.TaskDeltaLink,
                Title = source.Title,
                ColorHex = source.ColorHex,
                GroupId = source.GroupId,
                SortOrder = source.SortOrder,
                IsDefault = source.IsDefault,
                IsReadOnly = source.IsReadOnly,
                DeltaLink = source.DeltaLink,
                LastSuccessfulSyncUtc = source.LastSuccessfulSyncUtc,
                WatermarkUtc = source.WatermarkUtc,
                PendingMutation = source.PendingMutation,
                CreatedAtUtc = source.CreatedAtUtc,
                ModifiedAtUtc = source.ModifiedAtUtc
            };

    public static AccountTask Task(AccountTask source)
    {
        if (source is null)
            return null;

        return new AccountTask
        {
            Id = source.Id,
            MailAccountId = source.MailAccountId,
            TaskListId = source.TaskListId,
            SourceKind = source.SourceKind,
            RemoteId = source.RemoteId,
            RemoteVersion = source.RemoteVersion,
            Title = source.Title,
            Notes = source.Notes,
            DueDate = source.DueDate,
            IsCompleted = source.IsCompleted,
            IsImportant = source.IsImportant,
            MyDayDateUtc = source.MyDayDateUtc,
            CompletedAtUtc = source.CompletedAtUtc,
            RemoteOrder = source.RemoteOrder,
            PendingMutation = source.PendingMutation,
            CreatedAtUtc = source.CreatedAtUtc,
            ModifiedAtUtc = source.ModifiedAtUtc,
            IsReadOnly = source.IsReadOnly,
            Steps = source.Steps?.Select(TaskStep).ToList() ?? []
        };
    }

    public static AccountTaskStep TaskStep(AccountTaskStep source)
        => source is null
            ? null
            : new AccountTaskStep
            {
                Id = source.Id,
                TaskId = source.TaskId,
                MailAccountId = source.MailAccountId,
                SourceKind = source.SourceKind,
                RemoteId = source.RemoteId,
                RemoteVersion = source.RemoteVersion,
                Title = source.Title,
                IsCompleted = source.IsCompleted,
                Order = source.Order,
                PendingMutation = source.PendingMutation,
                CreatedAtUtc = source.CreatedAtUtc,
                ModifiedAtUtc = source.ModifiedAtUtc,
                IsReadOnly = source.IsReadOnly
            };

    public static ContactList ContactList(ContactList source)
        => source is null
            ? null
            : new ContactList
            {
                Id = source.Id,
                Name = source.Name,
                Description = source.Description,
                ColorHex = source.ColorHex,
                SortOrder = source.SortOrder,
                CreatedAtUtc = source.CreatedAtUtc,
                ModifiedAtUtc = source.ModifiedAtUtc
            };
}
