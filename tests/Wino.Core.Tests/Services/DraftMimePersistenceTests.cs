using FluentAssertions;
using MimeKit;
using Moq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class DraftMimePersistenceTests
{
    [Fact]
    public async Task Atomic_save_truncates_shorter_message_and_blocks_stale_remote_mime()
    {
        var root = Path.Combine(Path.GetTempPath(), "wino-draft-test-" + Guid.NewGuid());
        var native = new Mock<INativeAppService>(); native.Setup(x => x.GetMimeMessageStoragePath()).ReturnsAsync(root);
        var registry = new DraftUpdateRegistry();
        var files = new MimeFileService(native.Object, registry);
        var account = Guid.NewGuid(); var file = Guid.NewGuid();
        try
        {
            using var large = new MimeMessage { Subject = "large", Body = new TextPart("plain") { Text = new string('x', 10000) } };
            using var small = new MimeMessage { Subject = "small", Body = new TextPart("plain") { Text = "short" } };
            (await files.SaveDraftMimeMessageAsync(file, large, account)).Should().BeTrue();
            (await files.SaveDraftMimeMessageAsync(file, small, account)).Should().BeTrue();
            var draftId = Guid.NewGuid();
            registry.Protect(account, new MailCopy { UniqueId = draftId, Id = "old", FileId = file });
            await files.SaveMimeMessageAsync(file, large, account);
            registry.ConfirmIdentity(account, draftId, "new");
            registry.Release(account, draftId);
            await files.SaveRemoteMimeMessageAsync(file, large, account, "old");
            var path = Path.Combine(await files.GetMimeResourcePathAsync(account, file), "mail.eml");
            var bytes = await File.ReadAllBytesAsync(path);
            using var expected = new MemoryStream(); small.WriteTo(expected);
            bytes.Should().Equal(expected.ToArray());
            Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Should().BeEmpty();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
