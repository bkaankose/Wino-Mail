using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq.Expressions;
using System.Threading.Tasks;
using SQLite;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Services;

var databaseDirectory = Path.Combine(Path.GetTempPath(), $"WinoAotSmoke-{Guid.NewGuid():N}");
Directory.CreateDirectory(databaseDirectory);
SQLiteAsyncConnection? connection = null;

try
{
    var database = new DatabaseService(new SmokeConfiguration(databaseDirectory));
    await database.InitializeAsync().ConfigureAwait(false);
    connection = database.Connection;

    var mailCopyId = Guid.NewGuid();
    await ExerciseAsync(connection, new MailCopy { UniqueId = mailCopyId, Id = "mail", Subject = "before" }, item => item.UniqueId == mailCopyId, item => item.Subject = "after");

    var mailCategoryId = Guid.NewGuid();
    await ExerciseAsync(connection, new MailCategory { Id = mailCategoryId, Name = "before" }, item => item.Id == mailCategoryId, item => item.Name = "after");

    var categoryAssignmentId = Guid.NewGuid();
    await ExerciseAsync(connection, new MailCategoryAssignment { Id = categoryAssignmentId }, item => item.Id == categoryAssignmentId, item => item.MailCategoryId = Guid.NewGuid());

    var folderId = Guid.NewGuid();
    await ExerciseAsync(connection, new MailItemFolder { Id = folderId, FolderName = "before" }, item => item.Id == folderId, item => item.FolderName = "after");

    var accountId = Guid.NewGuid();
    await ExerciseAsync(connection, new MailAccount { Id = accountId, Name = "before", Address = "aot@example.test" }, item => item.Id == accountId, item => item.Name = "after");

    var contactId = Guid.NewGuid();
    await ExerciseAsync(connection, new AccountContact { Id = contactId, DisplayName = "before" }, item => item.Id == contactId, item => item.DisplayName = "after");

    var addressBookId = Guid.NewGuid();
    await ExerciseAsync(connection, new ContactAddressBook { Id = addressBookId, DisplayName = "before" }, item => item.Id == addressBookId, item => item.DisplayName = "after");

    var contactEmailId = Guid.NewGuid();
    await ExerciseAsync(connection, new ContactEmailAddress { Id = contactEmailId, ContactId = contactId, Address = "before@example.test" }, item => item.Id == contactEmailId, item => item.Address = "after@example.test");

    var serverId = Guid.NewGuid();
    await ExerciseAsync(connection, new CustomServerInformation { Id = serverId, DisplayName = "before" }, item => item.Id == serverId, item => item.DisplayName = "after");

    var signatureId = Guid.NewGuid();
    await ExerciseAsync(connection, new AccountSignature { Id = signatureId, Name = "before" }, item => item.Id == signatureId, item => item.Name = "after");

    var templateId = Guid.NewGuid();
    await ExerciseAsync(connection, new EmailTemplate { Id = templateId, Name = "before" }, item => item.Id == templateId, item => item.Name = "after");

    var mergedInboxId = Guid.NewGuid();
    await ExerciseAsync(connection, new MergedInbox { Id = mergedInboxId, Name = "before" }, item => item.Id == mergedInboxId, item => item.Name = "after");

    var preferencesId = Guid.NewGuid();
    var preferences = new MailAccountPreferences { Id = preferencesId };
    preferences.PrepareForStorage();
    await ExerciseAsync(
        connection,
        preferences,
        item => item.Id == preferencesId,
        item =>
        {
            item.IsNotificationsEnabled = true;
            item.PrepareForStorage();
        });

    var aliasId = Guid.NewGuid();
    await ExerciseAsync(connection, new MailAccountAlias { Id = aliasId, AliasAddress = "before@example.test" }, item => item.Id == aliasId, item => item.AliasAddress = "after@example.test");

    const string thumbnailDomain = "aot.example.test";
    await ExerciseAsync(connection, new Thumbnail { Domain = thumbnailDomain, GravatarFileName = "before" }, item => item.Domain == thumbnailDomain, item => item.GravatarFileName = "after");

    var shortcutId = Guid.NewGuid();
    await ExerciseAsync(connection, new KeyboardShortcut { Id = shortcutId, Key = "A" }, item => item.Id == shortcutId, item => item.Key = "B");

    var calendarId = Guid.NewGuid();
    await ExerciseAsync(connection, new AccountCalendar { Id = calendarId, Name = "before" }, item => item.Id == calendarId, item => item.Name = "after");

    var attendeeId = Guid.NewGuid();
    await ExerciseAsync(connection, new CalendarEventAttendee { Id = attendeeId, Name = "before" }, item => item.Id == attendeeId, item => item.Name = "after");

    var calendarItemId = Guid.NewGuid();
    await ExerciseAsync(connection, new CalendarItem { Id = calendarItemId, Title = "before" }, item => item.Id == calendarItemId, item => item.Title = "after");

    var attachmentId = Guid.NewGuid();
    await ExerciseAsync(connection, new CalendarAttachment { Id = attachmentId, FileName = "before" }, item => item.Id == attachmentId, item => item.FileName = "after");

    var reminderId = Guid.NewGuid();
    await ExerciseAsync(connection, new Reminder { Id = reminderId }, item => item.Id == reminderId, item => item.DurationInSeconds = 60);

    var invitationMappingId = Guid.NewGuid();
    await ExerciseAsync(connection, new MailInvitationCalendarMapping { Id = invitationMappingId, InvitationUid = "before" }, item => item.Id == invitationMappingId, item => item.InvitationUid = "after");

    var receiptId = Guid.NewGuid();
    await ExerciseAsync(connection, new SentMailReceiptState { MailUniqueId = receiptId, MessageId = "before" }, item => item.MailUniqueId == receiptId, item => item.MessageId = "after");

    var winoAccountId = Guid.NewGuid();
    await ExerciseAsync(connection, new WinoAccount { Id = winoAccountId, Email = "before@example.test" }, item => item.Id == winoAccountId, item => item.Email = "after@example.test");

    Console.WriteLine("Native AOT SQLite smoke test passed for all 24 entities.");
}
finally
{
    if (connection is not null)
    {
        await connection.CloseAsync().ConfigureAwait(false);
    }

    Directory.Delete(databaseDirectory, recursive: true);
}

[UnconditionalSuppressMessage(
    "Trimming",
    "IL2091",
    Justification = "Every closed entity type is explicitly rooted by Wino.Services/ILLink.Descriptors.xml and exercised by this Native AOT harness.")]
[UnconditionalSuppressMessage(
    "Trimming",
    "IL2026",
    Justification = "sqlite-net's object CRUD overloads require runtime metadata; all 24 runtime entity types are explicitly rooted by Wino.Services/ILLink.Descriptors.xml.")]
static async Task ExerciseAsync<T>(SQLiteAsyncConnection connection, T entity, Expression<Func<T, bool>> predicate, Action<T> mutate)
    where T : new()
{
    await connection.InsertAsync(entity).ConfigureAwait(false);

    var inserted = await connection.Table<T>().Where(predicate).FirstOrDefaultAsync().ConfigureAwait(false)
        ?? throw new InvalidOperationException($"Insert/query failed for {typeof(T).Name}.");

    mutate(inserted);
    await connection.UpdateAsync(inserted).ConfigureAwait(false);

    _ = await connection.Table<T>().Where(predicate).FirstOrDefaultAsync().ConfigureAwait(false)
        ?? throw new InvalidOperationException($"Update/query failed for {typeof(T).Name}.");

    await connection.DeleteAsync(inserted).ConfigureAwait(false);

    if (await connection.Table<T>().Where(predicate).FirstOrDefaultAsync().ConfigureAwait(false) is not null)
    {
        throw new InvalidOperationException($"Delete failed for {typeof(T).Name}.");
    }
}

file sealed class SmokeConfiguration(string databaseDirectory) : IApplicationConfiguration
{
    public string ApplicationDataFolderPath { get; set; } = databaseDirectory;
    public string PublisherSharedFolderPath { get; set; } = databaseDirectory;
    public string ApplicationTempFolderPath { get; set; } = databaseDirectory;
    public string SentryDNS => string.Empty;
}
