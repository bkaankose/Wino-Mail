using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public sealed class BackupRestorePageViewModelTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "wino-backup-tests", Guid.NewGuid().ToString("N"));

    public BackupRestorePageViewModelTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task ExportLocalData_WritesToTheExactPickedPath()
    {
        // A sandboxed save panel grants only the path the user confirmed, including a renamed file.
        var pickedPath = Path.Combine(_folder, "renamed by user.winosnap");
        string? suggestedName = null;
        var dialogs = new Mock<IMailDialogService>();
        dialogs.Setup(service => service.PickFilePathAsync(It.IsAny<string>()))
            .Callback<string>(name => suggestedName = name)
            .ReturnsAsync(pickedPath);
        var sync = new Mock<IWinoAccountDataSyncService>();
        sync.Setup(service => service.ExportToFileAsync(It.IsAny<WinoAccountSyncSelection>(), It.IsAny<SyncSnapshotSecretPrompt?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WinoAccountSyncFileExportResult { Content = [1, 2, 3], FileName = "wino-backup-20000101-0000.winosnap" });
        var viewModel = new BackupRestorePageViewModel(dialogs.Object, sync.Object, Mock.Of<IWinoAccountProfileService>());

        await viewModel.ExportLocalDataCommand.ExecuteAsync(null);

        suggestedName.Should().StartWith("wino-backup-").And.EndWith(".winosnap");
        File.ReadAllBytes(pickedPath).Should().Equal(1, 2, 3);
        Directory.GetFiles(_folder).Should().ContainSingle();
    }

    [Fact]
    public async Task ExportLocalData_Cancelled_DoesNotExport()
    {
        var dialogs = new Mock<IMailDialogService>();
        dialogs.Setup(service => service.PickFilePathAsync(It.IsAny<string>())).ReturnsAsync(string.Empty);
        var sync = new Mock<IWinoAccountDataSyncService>();
        var viewModel = new BackupRestorePageViewModel(dialogs.Object, sync.Object, Mock.Of<IWinoAccountProfileService>());

        await viewModel.ExportLocalDataCommand.ExecuteAsync(null);

        sync.Verify(service => service.ExportToFileAsync(It.IsAny<WinoAccountSyncSelection>(), It.IsAny<SyncSnapshotSecretPrompt?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
