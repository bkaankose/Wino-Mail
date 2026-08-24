using System.Collections.Specialized;
using FluentAssertions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Xunit;

namespace Wino.Mail.ViewModels.Tests.Collections;

public sealed class MenuItemCollectionTests
{
    [Fact]
    public async Task ReplaceFoldersAsync_PreservesExistingItemsWithoutReset()
    {
        var preservedItem = new NewMailMenuItem();
        var oldFolder = new MenuItemBase(Guid.NewGuid());
        var newFolder = new MenuItemBase(Guid.NewGuid());
        var collection = new MenuItemCollection(new ImmediateDispatcher())
        {
            preservedItem,
            new SeperatorItem(),
            oldFolder,
        };
        var actions = new List<NotifyCollectionChangedAction>();
        collection.CollectionChanged += (_, args) => actions.Add(args.Action);

        await collection.ReplaceFoldersAsync([newFolder]);

        collection.Should().ContainInOrder(preservedItem, collection.OfType<SeperatorItem>().Single(), newFolder);
        collection.Should().NotContain(oldFolder);
        actions.Should().NotContain(NotifyCollectionChangedAction.Reset);
        actions.Should().Contain(NotifyCollectionChangedAction.Remove);
        actions.Should().Contain(NotifyCollectionChangedAction.Add);
    }

    private sealed class ImmediateDispatcher : IDispatcher
    {
        public Task ExecuteOnUIThread(Action action)
        {
            action();
            return Task.CompletedTask;
        }
    }
}
