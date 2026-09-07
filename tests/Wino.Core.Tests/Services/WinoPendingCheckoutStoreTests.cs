using FluentAssertions;
using Moq;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class WinoPendingCheckoutStoreTests
{
    private readonly Dictionary<string, object> _values = [];
    private readonly Mock<IConfigurationService> _configuration = new();

    public WinoPendingCheckoutStoreTests()
    {
        _configuration.Setup(x => x.Set(It.IsAny<string>(), It.IsAny<object>()))
            .Callback((string key, object value) => _values[key] = value);
        _configuration.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string key, string fallback) => _values.TryGetValue(key, out var value) ? (string)value : fallback);
        _configuration.Setup(x => x.Remove(It.IsAny<string>())).Returns((string key) => _values.Remove(key));
    }

    [Fact]
    public void NewInstance_LoadsSavedAccountAndProduct()
    {
        var accountId = Guid.NewGuid();
        new WinoPendingCheckoutStore(_configuration.Object).Save(accountId, WinoAddOnProductType.AI_PACK);
        var restarted = new WinoPendingCheckoutStore(_configuration.Object);

        restarted.Get(accountId).Should().Be(WinoAddOnProductType.AI_PACK);
        restarted.Get(Guid.NewGuid()).Should().BeNull();
    }

    [Fact]
    public void Clear_DoesNotRemoveAnotherAccountsCheckout()
    {
        var accountId = Guid.NewGuid();
        var store = new WinoPendingCheckoutStore(_configuration.Object);
        store.Save(accountId, WinoAddOnProductType.UNLIMITED_ACCOUNTS);

        store.Clear(Guid.NewGuid());
        store.Get(accountId).Should().Be(WinoAddOnProductType.UNLIMITED_ACCOUNTS);
        store.Clear(accountId);
        store.Get(accountId).Should().BeNull();
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("{0}|999|9999999999")]
    [InlineData("{0}|0|0")]
    [InlineData("{0}|0|not-a-timestamp")]
    public void Get_IgnoresExpiredOrMalformedPersistedCheckout(string persisted)
    {
        var accountId = Guid.NewGuid();
        _values["WinoPendingCheckout"] = string.Format(persisted, accountId);

        new WinoPendingCheckoutStore(_configuration.Object).Get(accountId).Should().BeNull();
    }
}
