using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Exchange.WebServices.Data;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Requests.Category;
using Wino.Core.Requests.Mail;
using Wino.Providers.Exchange.Requests.Bundles;
using Task = System.Threading.Tasks.Task;

namespace Wino.Providers.Exchange.Synchronizers.Exchange;

/// <summary>
/// Mail categories. A mail's categories are names on the item (EWS t:Categories, MS-OXWSCORE 2.2.4.24; MAPI
/// PidNameKeywords). The master list with colours is the "CategoryList" configuration message of the Calendar folder
/// (MS-OXOCFG 2.2.5.2.2), reached over EWS as a user configuration (MS-OXWSUSRCFG). A mailbox has no list until a
/// client creates one: then categories come from the names on items, without colour, and reading never creates it.
/// The MAPI synchronizer replaces the three transport primitives.
/// </summary>
public partial class ExchangeSynchronizer
{
    private const string CategoryListConfigurationName = "CategoryList";

    private bool _categoryListRefreshedThisPass;

    /// <summary>A category write for one mail: its server id, the local folder it is in when known, and its full name list.</summary>
    protected sealed record MailCategoryWrite(string MessageId, Guid? FolderId, IReadOnlyList<string> CategoryNames);

    /// <summary>Starts a mail pass: a name missing locally may refresh the master list once per pass.</summary>
    protected void BeginCategoryPass() => _categoryListRefreshedThisPass = false;

    protected override async Task SynchronizeCategoriesAsync(CancellationToken cancellationToken = default)
    {
        if (MailCategoryService is not { } categoryService)
            return;

        var list = await ReadCategoryListAsync(cancellationToken).ConfigureAwait(false);
        if (list is null)
        {
            _logger.Debug("Exchange mailbox {Account} has no category list; categories come from the mails.", Account.Name);
            return;
        }

        var incoming = list.Categories
            .Select(entry =>
            {
                var colors = ExchangeCategoryColors.ToOption(entry.Color);
                return new MailCategory
                {
                    MailAccountId = Account.Id,
                    RemoteId = entry.Guid,
                    Name = entry.Name,
                    BackgroundColorHex = colors.BackgroundColorHex,
                    TextColorHex = colors.TextColorHex,
                    Source = MailCategorySource.Exchange
                };
            })
            .ToList();

        // A name on mails that the list does not have stays as a category without colour, as Outlook shows it ("not in
        // the master category list"); only one no mail carries any more goes.
        var existing = await categoryService.GetCategoriesAsync(Account.Id).ConfigureAwait(false);
        foreach (var category in existing.Where(c => c.Source == MailCategorySource.Exchange))
        {
            var isListed = incoming.Any(entry =>
                (!string.IsNullOrEmpty(category.RemoteId) && string.Equals(entry.RemoteId, category.RemoteId, StringComparison.OrdinalIgnoreCase))
                || string.Equals(entry.Name, category.Name?.Trim(), StringComparison.OrdinalIgnoreCase));

            if (isListed)
                continue;

            var mails = await categoryService.GetMailCopiesForCategoryAsync(category.Id).ConfigureAwait(false);
            if (mails.Count == 0)
                continue;

            var noColor = ExchangeCategoryColors.ToOption(ExchangeCategoryColors.NoColor);
            incoming.Add(new MailCategory
            {
                Id = category.Id,
                MailAccountId = Account.Id,
                Name = category.Name,
                BackgroundColorHex = noColor.BackgroundColorHex,
                TextColorHex = noColor.TextColorHex,
                Source = MailCategorySource.Exchange
            });
        }

        await categoryService.ReplaceCategoriesAsync(Account.Id, incoming).ConfigureAwait(false);
    }

    #region Item categories

    /// <summary>
    /// The category names of an item from a property set that asked for them, with every name known locally. Null when
    /// the item was read without categories, so its assignments stay as they are.
    /// </summary>
    private async Task<IReadOnlyList<string>?> PrepareItemCategoriesAsync(Item item, CancellationToken cancellationToken)
    {
        var categories = SafeGet(() => item.Categories);
        if (categories is null)
            return null;

        var names = NormalizeCategoryNames(categories);
        await EnsureMailCategoriesAsync(names, cancellationToken).ConfigureAwait(false);
        return names;
    }

    protected static List<string> NormalizeCategoryNames(IEnumerable<string?> names)
        => [.. names
            .Select(name => name?.Trim())
            .OfType<string>()
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// Makes every name a local category so the assignment can be stored. A name the list does not have yet refreshes
    /// the list once per pass (it may have been added elsewhere); what is still missing becomes a category without colour.
    /// </summary>
    protected async Task EnsureMailCategoriesAsync(IReadOnlyList<string> names, CancellationToken cancellationToken)
    {
        if (MailCategoryService is not { } categoryService || names.Count == 0)
            return;

        var missing = await GetMissingCategoryNamesAsync(categoryService, names).ConfigureAwait(false);
        if (missing.Count == 0)
            return;

        if (!_categoryListRefreshedThisPass)
        {
            _categoryListRefreshedThisPass = true;
            try
            {
                await SynchronizeCategoriesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Reading the Exchange category list of {Account} failed while mails carried unknown categories {Categories}.",
                    Account.Name, string.Join(", ", missing));
            }

            missing = await GetMissingCategoryNamesAsync(categoryService, names).ConfigureAwait(false);
        }

        var noColor = ExchangeCategoryColors.ToOption(ExchangeCategoryColors.NoColor);
        foreach (var name in missing)
        {
            await categoryService.CreateCategoryAsync(new MailCategory
            {
                Id = Guid.NewGuid(),
                MailAccountId = Account.Id,
                Name = name,
                BackgroundColorHex = noColor.BackgroundColorHex,
                TextColorHex = noColor.TextColorHex,
                Source = MailCategorySource.Exchange
            }).ConfigureAwait(false);
        }
    }

    /// <summary>Stores a known mail's categories as the server reported them.</summary>
    protected async Task ReplaceMailCategoryAssignmentsAsync(Guid mailUniqueId, IReadOnlyList<string> names)
    {
        if (MailCategoryService is { } categoryService)
            await categoryService.ReplaceMailAssignmentsAsync(Account.Id, mailUniqueId, names).ConfigureAwait(false);
    }

    private async Task<List<string>> GetMissingCategoryNamesAsync(IMailCategoryService categoryService, IReadOnlyList<string> names)
    {
        var known = (await categoryService.GetCategoriesAsync(Account.Id).ConfigureAwait(false))
            .Select(category => category.Name?.Trim())
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return [.. names.Where(name => !known.Contains(name))];
    }

    #endregion

    #region Requests

    public override List<IRequestBundle<EwsRequest>> UpdateCategories(BatchMailCategoryAssignmentRequest request)
        => [.. request.SelectMany(assignment => CreateBundle(
            (_, cancellationToken) => WriteMailCategoriesAsync([new MailCategoryWrite(assignment.Item.Id, assignment.Item.FolderId, assignment.CategoryNames ?? [])], cancellationToken),
            assignment, assignment))];

    public override List<IRequestBundle<EwsRequest>> CreateCategory(MailCategoryCreateRequest request)
    {
        // A category that only exists on the device has no entry in the mailbox's list.
        if (request.Category.Source != MailCategorySource.Exchange)
            return [];

        var category = request.Category;
        return CreateBundle(async (_, cancellationToken) =>
        {
            string? guid = null;

            // The first category the user creates is what creates the list.
            await EditCategoryListAsync(list =>
            {
                list ??= ExchangeCategoryList.CreateEmpty();
                guid = list.Add(category.Name, ExchangeCategoryColors.ToColor(category, list.ColorOf(null, category.Name)));
                return list;
            }, cancellationToken).ConfigureAwait(false);

            await StoreCategoryRemoteIdAsync(category.Id, guid).ConfigureAwait(false);
        }, request, request);
    }

    public override List<IRequestBundle<EwsRequest>> UpdateCategory(MailCategoryUpdateRequest request)
    {
        if (request.Category.Source != MailCategorySource.Exchange)
            return [];

        var category = request.Category;
        return CreateBundle(async (_, cancellationToken) =>
        {
            string? guid = null;

            // A category known only from mails gets its entry when the user edits it.
            await EditCategoryListAsync(list =>
            {
                list ??= ExchangeCategoryList.CreateEmpty();
                var storedColor = list.ColorOf(request.PreviousRemoteId, request.PreviousName);
                guid = list.Upsert(request.PreviousRemoteId, request.PreviousName, category.Name, ExchangeCategoryColors.ToColor(category, storedColor));
                return list;
            }, cancellationToken).ConfigureAwait(false);

            await StoreCategoryRemoteIdAsync(category.Id, guid).ConfigureAwait(false);

            // Mails carry categories by name, so a rename is written to every mail that has the old name.
            var hasNameChanged = !string.Equals(request.PreviousName, category.Name, StringComparison.Ordinal);
            if (hasNameChanged && request.AffectedMessages is { Count: > 0 } affected)
            {
                await WriteMailCategoriesAsync(
                    [.. affected.Select(target => new MailCategoryWrite(target.MessageId, null, target.CategoryNames ?? []))],
                    cancellationToken).ConfigureAwait(false);
            }
        }, request, request);
    }

    /// <summary>Removes the category from the list. Mails keep the name, as when Outlook deletes a category.</summary>
    public override List<IRequestBundle<EwsRequest>> DeleteCategory(MailCategoryDeleteRequest request)
    {
        if (request.Category.Source != MailCategorySource.Exchange)
            return [];

        return CreateBundle(
            (_, cancellationToken) => EditCategoryListAsync(
                list => list is not null && list.Remove(request.PreviousRemoteId, request.Category.Name) ? list : null,
                cancellationToken),
            request, request);
    }

    private async Task StoreCategoryRemoteIdAsync(Guid categoryId, string? guid)
    {
        if (MailCategoryService is { } categoryService && !string.IsNullOrEmpty(guid))
            await categoryService.UpdateRemoteIdAsync(categoryId, guid).ConfigureAwait(false);
    }

    #endregion

    #region EWS transport

    /// <summary>The mailbox's category list, or null when it has none.</summary>
    protected virtual async Task<ExchangeCategoryList?> ReadCategoryListAsync(CancellationToken cancellationToken)
    {
        var service = await CreateServiceAsync(cancellationToken).ConfigureAwait(false);
        var configuration = await BindCategoryListAsync(service, cancellationToken).ConfigureAwait(false);
        return configuration?.XmlData is { Length: > 0 } xml ? ExchangeCategoryList.Parse(xml) : null;
    }

    /// <summary>
    /// Reads the list, hands it to <paramref name="edit"/> (null when the mailbox has none) and writes what the edit
    /// returns; a null result writes nothing. The configuration is created (CreateUserConfiguration) only when the
    /// mailbox has none, otherwise updated in place (UpdateUserConfiguration).
    /// </summary>
    protected virtual async Task EditCategoryListAsync(Func<ExchangeCategoryList?, ExchangeCategoryList?> edit, CancellationToken cancellationToken)
    {
        var service = await CreateServiceAsync(cancellationToken).ConfigureAwait(false);
        var configuration = await BindCategoryListAsync(service, cancellationToken).ConfigureAwait(false);
        var current = configuration?.XmlData is { Length: > 0 } xml ? ExchangeCategoryList.Parse(xml) : null;

        var edited = edit(current);
        if (edited is null)
            return;

        if (edited.IsEmpty)
        {
            if (configuration is not null)
                await configuration.Delete(cancellationToken).ConfigureAwait(false);

            return;
        }

        var bytes = edited.Serialize();
        if (configuration is null)
        {
            var created = new UserConfiguration(service) { XmlData = bytes };
            await created.Save(CategoryListConfigurationName, new FolderId(WellKnownFolderName.Calendar), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            configuration.XmlData = bytes;
            await configuration.Update(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Writes each mail's full category list onto the item (UpdateItem, SetItemField t:Categories).</summary>
    protected virtual async Task WriteMailCategoriesAsync(IReadOnlyList<MailCategoryWrite> writes, CancellationToken cancellationToken)
    {
        var service = await CreateServiceAsync(cancellationToken).ConfigureAwait(false);
        foreach (var write in writes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = await Item.Bind(service, new ItemId(write.MessageId), new PropertySet(BasePropertySet.IdOnly, ItemSchema.Categories)).ConfigureAwait(false);
            item.Categories.Clear();
            item.Categories.AddRange(write.CategoryNames);
            await item.Update(ConflictResolutionMode.AutoResolve).ConfigureAwait(false);
        }
    }

    private static async Task<UserConfiguration?> BindCategoryListAsync(ExchangeService service, CancellationToken cancellationToken)
    {
        try
        {
            return await UserConfiguration.Bind(service, CategoryListConfigurationName, new FolderId(WellKnownFolderName.Calendar),
                UserConfigurationProperties.XmlData, cancellationToken).ConfigureAwait(false);
        }
        catch (ServiceResponseException ex) when (ex.ErrorCode == ServiceError.ErrorItemNotFound)
        {
            return null;
        }
    }

    #endregion
}
