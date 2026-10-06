using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Wino.Core.Domain.Interfaces;
using Wino.Services;
using Wino.Services.Dav;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class DavCredentialStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"wino-dav-{Guid.NewGuid():N}");
    private readonly Guid _accountId = Guid.NewGuid();

    [Fact]
    public async Task SaveOverwriteReadDelete_PreservesPathEncodingAndContext()
    {
        var protector = new RecordingProtector();
        var store = CreateStore(protector);
        (await store.GetPasswordAsync(_accountId)).Should().BeNull();

        await store.SavePasswordAsync(_accountId, "first");
        await store.SavePasswordAsync(_accountId, "päss 密碼");

        File.Exists(CredentialPath).Should().BeTrue();
        (await store.GetPasswordAsync(_accountId)).Should().Be("päss 密碼");
        protector.LastContext.Should().Equal(SHA256.HashData(Encoding.UTF8.GetBytes($"Wino.DAV.{_accountId:D}")));
        protector.LastInput.Should().OnlyContain(value => value == 0);
        protector.LastOutput.Should().OnlyContain(value => value == 0);

        await store.DeleteAsync(_accountId);
        await store.DeleteAsync(_accountId);
        (await store.GetPasswordAsync(_accountId)).Should().BeNull();
    }

    [Fact]
    public async Task CanceledOverwrite_PreservesExistingSecretAndRemovesTemporaryFile()
    {
        var protector = new RecordingProtector();
        var store = CreateStore(protector);
        await store.SavePasswordAsync(_accountId, "existing");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var act = () => store.SavePasswordAsync(_accountId, "replacement", cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        protector.LastInput.Should().OnlyContain(value => value == 0);
        protector.LastOutput.Should().OnlyContain(value => value == 0);
        Directory.GetFiles(Path.GetDirectoryName(CredentialPath)!, "*.tmp").Should().BeEmpty();
        (await store.GetPasswordAsync(_accountId)).Should().Be("existing");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnreadableSecret_PropagatesFailureWithoutRemovingFile(bool unavailable)
    {
        await CreateStore(new RecordingProtector()).SavePasswordAsync(_accountId, "existing");
        var protector = new RecordingProtector { Unavailable = unavailable, Corrupt = !unavailable };
        var act = () => CreateStore(protector).GetPasswordAsync(_accountId);

        if (unavailable)
            await act.Should().ThrowAsync<PlatformNotSupportedException>();
        else
            await act.Should().ThrowAsync<CryptographicException>();

        File.Exists(CredentialPath).Should().BeTrue();
        protector.LastInput.Should().OnlyContain(value => value == 0);
        (await CreateStore(new RecordingProtector()).GetPasswordAsync(_accountId)).Should().Be("existing");
    }

    private string CredentialPath => Path.Combine(_root, "credentials", "dav", $"{_accountId:N}.bin");

    private DavCredentialStore CreateStore(ISecretProtector protector)
        => new(new ApplicationConfiguration { ApplicationDataFolderPath = _root }, protector);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class RecordingProtector : ISecretProtector
    {
        public bool Unavailable { get; init; }
        public bool Corrupt { get; init; }
        public byte[] LastInput { get; private set; } = [];
        public byte[] LastOutput { get; private set; } = [];
        public byte[] LastContext { get; private set; } = [];

        public byte[] Protect(byte[] data, byte[] context) => Transform(data, context);

        public byte[] Unprotect(byte[] data, byte[] context)
        {
            LastInput = data;
            LastContext = context.ToArray();

            if (Unavailable) throw new PlatformNotSupportedException("Protection is unavailable.");
            if (Corrupt) throw new CryptographicException("The secret is corrupt.");

            return Transform(data, context);
        }

        private byte[] Transform(byte[] data, byte[] context)
        {
            LastInput = data;
            LastContext = context.ToArray();
            LastOutput = data.Select((value, index) => (byte)(value ^ context[index % context.Length])).ToArray();
            return LastOutput;
        }
    }
}
