using Microsoft.EntityFrameworkCore;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Services;

/// <summary>
/// EF Core 10 DbContext for Wino Mail application.
/// 
/// MIGRATION STATUS: Code-first database context with 14 tables configured.
/// 
/// KEY FEATURES:
/// - Change tracking is DISABLED globally (QueryTrackingBehavior.NoTracking)
/// - All relationships configured via Fluent API
/// - Proper indexes configured for performance
/// - Foreign key relationships with appropriate delete behaviors
/// - Navigation properties marked with [NotMapped] - loaded manually by services
/// 
/// TODO: EFCore - Next Steps for Developer:
/// 1. Update DatabaseService to use WinoDbContext instead of SQLiteAsyncConnection
/// 2. Update BaseDatabaseService to inject DbContext instead of IDatabaseService.Connection
/// 3. Replace all Connection.Table<T>() calls with context.Set<T>()
/// 4. Replace Connection.InsertAsync() with context.Add() + context.SaveChangesAsync()
/// 5. Replace Connection.UpdateAsync() with change tracking + context.SaveChangesAsync()
/// 6. Replace Connection.DeleteAsync() with context.Remove() + context.SaveChangesAsync()
/// 7. Rewrite SqlKata queries to LINQ or use FromSqlRaw() for raw SQL
/// 8. Update transaction handling from RunInTransactionAsync to context.Database.BeginTransactionAsync()
/// 9. Test manual relationship loading patterns (AssignedFolder, AssignedAccount, etc.)
/// 10. Create initial migration: dotnet ef migrations add InitialCreate
/// 11. Test migration on existing database for data preservation
/// 
/// TABLES (14):
/// Mail: MailCopy, MailItemFolder, MailAccountAlias, AccountSignature, MergedInbox
/// Calendar: AccountCalendar, CalendarItem, CalendarEventAttendee, Reminder
/// Shared: MailAccount, MailAccountPreferences, AccountContact, CustomServerInformation, KeyboardShortcut, Thumbnail
/// </summary>
public class WinoDbContext : DbContext
{
    public WinoDbContext(DbContextOptions<WinoDbContext> options) : base(options)
    {
    }

    // Mail entities
    public DbSet<MailCopy> MailCopies { get; set; }
    public DbSet<MailItemFolder> MailItemFolders { get; set; }
    public DbSet<MailAccountAlias> MailAccountAliases { get; set; }
    public DbSet<AccountSignature> AccountSignatures { get; set; }
    public DbSet<MergedInbox> MergedInboxes { get; set; }

    // Calendar entities
    public DbSet<AccountCalendar> AccountCalendars { get; set; }
    public DbSet<CalendarItem> CalendarItems { get; set; }
    public DbSet<CalendarEventAttendee> CalendarEventAttendees { get; set; }
    public DbSet<Reminder> Reminders { get; set; }

    // Shared entities
    public DbSet<MailAccount> MailAccounts { get; set; }
    public DbSet<MailAccountPreferences> MailAccountPreferences { get; set; }
    public DbSet<AccountContact> AccountContacts { get; set; }
    public DbSet<CustomServerInformation> CustomServerInformations { get; set; }
    public DbSet<KeyboardShortcut> KeyboardShortcuts { get; set; }
    public DbSet<Thumbnail> Thumbnails { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);

        // Disable change tracking globally for performance
        optionsBuilder.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        ConfigureMailEntities(modelBuilder);
        ConfigureCalendarEntities(modelBuilder);
        ConfigureSharedEntities(modelBuilder);
    }

    private void ConfigureMailEntities(ModelBuilder modelBuilder)
    {
        // MailCopy configuration
        modelBuilder.Entity<MailCopy>(entity =>
        {
            entity.ToTable("MailCopy");
            entity.HasKey(e => e.UniqueId);

            // Indexes for performance
            entity.HasIndex(e => e.Id).HasDatabaseName("IX_MailCopy_Id");
            entity.HasIndex(e => e.FolderId).HasDatabaseName("IX_MailCopy_FolderId");
            entity.HasIndex(e => e.ThreadId).HasDatabaseName("IX_MailCopy_ThreadId");
            entity.HasIndex(e => e.CreationDate).HasDatabaseName("IX_MailCopy_CreationDate");
            entity.HasIndex(e => e.IsRead).HasDatabaseName("IX_MailCopy_IsRead");
            entity.HasIndex(e => e.IsFlagged).HasDatabaseName("IX_MailCopy_IsFlagged");
            entity.HasIndex(e => e.IsFocused).HasDatabaseName("IX_MailCopy_IsFocused");
            entity.HasIndex(e => e.HasAttachments).HasDatabaseName("IX_MailCopy_HasAttachments");
            entity.HasIndex(e => e.IsDraft).HasDatabaseName("IX_MailCopy_IsDraft");

            // Foreign key relationship to MailItemFolder
            entity.HasOne<MailItemFolder>()
                .WithMany()
                .HasForeignKey(e => e.FolderId)
                .OnDelete(DeleteBehavior.Cascade);

            // Ignore navigation properties - these are loaded manually
            entity.Ignore(e => e.AssignedFolder);
            entity.Ignore(e => e.AssignedAccount);
            entity.Ignore(e => e.SenderContact);
        });

        // MailItemFolder configuration
        modelBuilder.Entity<MailItemFolder>(entity =>
        {
            entity.ToTable("MailItemFolder");
            entity.HasKey(e => e.Id);

            // Indexes
            entity.HasIndex(e => e.MailAccountId).HasDatabaseName("IX_MailItemFolder_MailAccountId");
            entity.HasIndex(e => e.RemoteFolderId).HasDatabaseName("IX_MailItemFolder_RemoteFolderId");
            entity.HasIndex(e => e.ParentRemoteFolderId).HasDatabaseName("IX_MailItemFolder_ParentRemoteFolderId");
            entity.HasIndex(e => e.SpecialFolderType).HasDatabaseName("IX_MailItemFolder_SpecialFolderType");
            entity.HasIndex(e => new { e.MailAccountId, e.RemoteFolderId })
                .HasDatabaseName("IX_MailItemFolder_Account_RemoteFolder");

            // Foreign key relationship to MailAccount
            entity.HasOne<MailAccount>()
                .WithMany()
                .HasForeignKey(e => e.MailAccountId)
                .OnDelete(DeleteBehavior.Cascade);

            // Ignore navigation properties
            entity.Ignore(e => e.ChildFolders);
        });

        // MailAccountAlias configuration
        modelBuilder.Entity<MailAccountAlias>(entity =>
        {
            entity.ToTable("MailAccountAlias");
            entity.HasKey(e => e.Id);

            // Indexes
            entity.HasIndex(e => e.AccountId).HasDatabaseName("IX_MailAccountAlias_AccountId");
            entity.HasIndex(e => e.AliasAddress).HasDatabaseName("IX_MailAccountAlias_AliasAddress");
            entity.HasIndex(e => new { e.AccountId, e.IsPrimary })
                .HasDatabaseName("IX_MailAccountAlias_Account_Primary");

            // Foreign key relationship to MailAccount
            entity.HasOne<MailAccount>()
                .WithMany()
                .HasForeignKey(e => e.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // AccountSignature configuration
        modelBuilder.Entity<AccountSignature>(entity =>
        {
            entity.ToTable("AccountSignature");
            entity.HasKey(e => e.Id);

            // Index
            entity.HasIndex(e => e.MailAccountId).HasDatabaseName("IX_AccountSignature_MailAccountId");

            // Foreign key relationship to MailAccount
            entity.HasOne<MailAccount>()
                .WithMany()
                .HasForeignKey(e => e.MailAccountId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // MergedInbox configuration
        modelBuilder.Entity<MergedInbox>(entity =>
        {
            entity.ToTable("MergedInbox");
            entity.HasKey(e => e.Id);
        });
    }

    private void ConfigureCalendarEntities(ModelBuilder modelBuilder)
    {
        // AccountCalendar configuration
        modelBuilder.Entity<AccountCalendar>(entity =>
        {
            entity.ToTable("AccountCalendar");
            entity.HasKey(e => e.Id);

            // Indexes
            entity.HasIndex(e => e.AccountId).HasDatabaseName("IX_AccountCalendar_AccountId");
            entity.HasIndex(e => e.RemoteCalendarId).HasDatabaseName("IX_AccountCalendar_RemoteCalendarId");
            entity.HasIndex(e => new { e.AccountId, e.IsPrimary })
                .HasDatabaseName("IX_AccountCalendar_Account_Primary");

            // Foreign key relationship to MailAccount
            entity.HasOne<MailAccount>()
                .WithMany()
                .HasForeignKey(e => e.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // CalendarItem configuration
        modelBuilder.Entity<CalendarItem>(entity =>
        {
            entity.ToTable("CalendarItem");
            entity.HasKey(e => e.Id);

            // Indexes
            entity.HasIndex(e => e.CalendarId).HasDatabaseName("IX_CalendarItem_CalendarId");
            entity.HasIndex(e => e.RemoteEventId).HasDatabaseName("IX_CalendarItem_RemoteEventId");
            entity.HasIndex(e => e.StartDate).HasDatabaseName("IX_CalendarItem_StartDate");
            entity.HasIndex(e => e.RecurringCalendarItemId).HasDatabaseName("IX_CalendarItem_RecurringParentId");
            entity.HasIndex(e => new { e.CalendarId, e.StartDate })
                .HasDatabaseName("IX_CalendarItem_Calendar_StartDate");

            // Foreign key relationship to AccountCalendar
            entity.HasOne<AccountCalendar>()
                .WithMany()
                .HasForeignKey(e => e.CalendarId)
                .OnDelete(DeleteBehavior.Cascade);

            // Self-referencing relationship for recurring events
            entity.HasOne<CalendarItem>()
                .WithMany()
                .HasForeignKey(e => e.RecurringCalendarItemId)
                .OnDelete(DeleteBehavior.Restrict);

            // Ignore computed/navigation properties
            entity.Ignore(e => e.Period);
        });

        // CalendarEventAttendee configuration
        modelBuilder.Entity<CalendarEventAttendee>(entity =>
        {
            entity.ToTable("CalendarEventAttendee");
            entity.HasKey(e => e.Id);

            // Index
            entity.HasIndex(e => e.CalendarItemId).HasDatabaseName("IX_CalendarEventAttendee_CalendarItemId");

            // Foreign key relationship to CalendarItem
            entity.HasOne<CalendarItem>()
                .WithMany()
                .HasForeignKey(e => e.CalendarItemId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Reminder configuration
        modelBuilder.Entity<Reminder>(entity =>
        {
            entity.ToTable("Reminder");
            entity.HasKey(e => e.Id);

            // Index
            entity.HasIndex(e => e.CalendarItemId).HasDatabaseName("IX_Reminder_CalendarItemId");

            // Foreign key relationship to CalendarItem
            entity.HasOne<CalendarItem>()
                .WithMany()
                .HasForeignKey(e => e.CalendarItemId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private void ConfigureSharedEntities(ModelBuilder modelBuilder)
    {
        // MailAccount configuration
        modelBuilder.Entity<MailAccount>(entity =>
        {
            entity.ToTable("MailAccount");
            entity.HasKey(e => e.Id);

            // Indexes
            entity.HasIndex(e => e.Address).HasDatabaseName("IX_MailAccount_Address");
            entity.HasIndex(e => e.ProviderType).HasDatabaseName("IX_MailAccount_ProviderType");
            entity.HasIndex(e => e.MergedInboxId).HasDatabaseName("IX_MailAccount_MergedInboxId");
            entity.HasIndex(e => e.Order).HasDatabaseName("IX_MailAccount_Order");

            // Foreign key relationship to MergedInbox (nullable)
            entity.HasOne<MergedInbox>()
                .WithMany()
                .HasForeignKey(e => e.MergedInboxId)
                .OnDelete(DeleteBehavior.SetNull);

            // Ignore navigation properties
            entity.Ignore(e => e.MergedInbox);
            entity.Ignore(e => e.ServerInformation);
            entity.Ignore(e => e.Preferences);
        });

        // MailAccountPreferences configuration
        modelBuilder.Entity<MailAccountPreferences>(entity =>
        {
            entity.ToTable("MailAccountPreferences");
            entity.HasKey(e => e.AccountId);

            // Foreign key relationship to MailAccount (one-to-one)
            entity.HasOne<MailAccount>()
                .WithOne()
                .HasForeignKey<MailAccountPreferences>(e => e.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // AccountContact configuration
        modelBuilder.Entity<AccountContact>(entity =>
        {
            entity.ToTable("AccountContact");
            entity.HasKey(e => e.Address);

            // Indexes
            entity.HasIndex(e => e.Name).HasDatabaseName("IX_AccountContact_Name");
            entity.HasIndex(e => e.Address).HasDatabaseName("IX_AccountContact_Address");
        });

        // CustomServerInformation configuration
        modelBuilder.Entity<CustomServerInformation>(entity =>
        {
            entity.ToTable("CustomServerInformation");
            entity.HasKey(e => e.Id);

            // Index
            entity.HasIndex(e => e.AccountId).HasDatabaseName("IX_CustomServerInformation_AccountId");

            // Foreign key relationship to MailAccount (one-to-one)
            entity.HasOne<MailAccount>()
                .WithOne()
                .HasForeignKey<CustomServerInformation>(e => e.AccountId)
                .OnDelete(DeleteBehavior.Cascade);

            // Ignore deprecated property
            entity.Ignore(e => e.DisplayName);
        });

        // KeyboardShortcut configuration
        modelBuilder.Entity<KeyboardShortcut>(entity =>
        {
            entity.ToTable("KeyboardShortcut");
            entity.HasKey(e => e.Id);

            // Indexes
            entity.HasIndex(e => e.MailOperation).HasDatabaseName("IX_KeyboardShortcut_MailOperation");
        });

        // Thumbnail configuration
        modelBuilder.Entity<Thumbnail>(entity =>
        {
            entity.ToTable("Thumbnail");
            entity.HasKey(e => e.Id);

            // Index
            entity.HasIndex(e => e.Address).HasDatabaseName("IX_Thumbnail_Address");
        });
    }
}
