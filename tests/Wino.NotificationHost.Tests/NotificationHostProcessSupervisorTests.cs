using System.Diagnostics;
using FluentAssertions;
using Wino.Mail.WinUI.Services;
using Xunit;

namespace Wino.NotificationHost.Tests;

public sealed class NotificationHostProcessSupervisorTests
{
    [Fact]
    public async Task OwnerProcessTermination_KillsAssignedHostsWithoutManagedDisposal()
    {
        var info = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true
        };
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Wino.NotificationHost.LifetimeProbe.dll"));
        info.ArgumentList.Add("owner-crash");
        using var owner = Process.Start(info)!;
        Process? host = null;

        try
        {
            var pid = await owner.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            host = Process.GetProcessById(int.Parse(pid!));
            _ = host.SafeHandle;
            await owner.StandardInput.WriteLineAsync("crash");
            await owner.StandardInput.FlushAsync();

            await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            owner.ExitCode.Should().Be(2);
            host.HasExited.Should().BeTrue();
        }
        finally
        {
            if (!owner.HasExited)
                owner.Kill();

            if (host != null)
            {
                if (!host.HasExited)
                    host.Kill();

                host.Dispose();
            }
        }
    }

    [Fact]
    public async Task HostWatchdog_TerminatesEvenWhenShutdownBlocksAfterMainReturns()
    {
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Wino.NotificationHost.LifetimeProbe.dll"));
        using var process = Process.Start(info)!;

        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            process.ExitCode.Should().Be(2);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
                await process.WaitForExitAsync();
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task SuperviseAsync_ObservesTheActualExitCode(int exitCode)
    {
        using var supervisor = new NotificationHostProcessSupervisor();
        using var process = StartHelper($"exit {exitCode}");

        var actual = await supervisor.SuperviseAsync(process, TimeSpan.FromSeconds(15), CancellationToken.None);

        actual.Should().Be(exitCode);
        process.HasExited.Should().BeTrue();
    }

    [Fact]
    public async Task SuperviseAsync_TimeoutTerminatesTheHostBeforeReturning()
    {
        using var supervisor = new NotificationHostProcessSupervisor();
        using var process = StartHelper("Start-Sleep -Seconds 60");

        var action = () => supervisor.SuperviseAsync(process, TimeSpan.FromMilliseconds(200), CancellationToken.None);

        await action.Should().ThrowAsync<TimeoutException>();
        process.HasExited.Should().BeTrue();
    }

    [Fact]
    public async Task SuperviseAsync_CancellationTerminatesTheHostBeforeReturning()
    {
        using var supervisor = new NotificationHostProcessSupervisor();
        using var process = StartHelper("Start-Sleep -Seconds 60");
        using var cancellation = new CancellationTokenSource();
        var completion = supervisor.SuperviseAsync(process, TimeSpan.FromSeconds(15), cancellation.Token);

        cancellation.Cancel();

        await ((Func<Task>)(async () => await completion)).Should().ThrowAsync<OperationCanceledException>();
        process.HasExited.Should().BeTrue();
    }

    [Fact]
    public async Task Dispose_TerminatesAllAssignedHostsAndLeavesOtherProcessesAlone()
    {
        using var supervisor = new NotificationHostProcessSupervisor();
        using var first = StartHelper("Start-Sleep -Seconds 60");
        using var second = StartHelper("Start-Sleep -Seconds 60");
        using var unrelated = StartHelper("Start-Sleep -Seconds 60");

        try
        {
            var firstCompletion = supervisor.SuperviseAsync(first, TimeSpan.FromSeconds(15), CancellationToken.None);
            var secondCompletion = supervisor.SuperviseAsync(second, TimeSpan.FromSeconds(15), CancellationToken.None);

            supervisor.Dispose();
            supervisor.Dispose();
            await Task.WhenAll(firstCompletion, secondCompletion);

            first.HasExited.Should().BeTrue();
            second.HasExited.Should().BeTrue();
            unrelated.HasExited.Should().BeFalse();
        }
        finally
        {
            unrelated.Kill();
            await unrelated.WaitForExitAsync();
        }
    }

    [Fact]
    public async Task SuperviseAsync_AfterDisposalDoesNotAbandonTheHost()
    {
        using var supervisor = new NotificationHostProcessSupervisor();
        supervisor.Dispose();
        using var process = StartHelper("Start-Sleep -Seconds 60");

        var action = () => supervisor.SuperviseAsync(process, TimeSpan.FromSeconds(15), CancellationToken.None);

        await action.Should().ThrowAsync<ObjectDisposedException>();
        process.HasExited.Should().BeTrue();
    }

    private static Process StartHelper(string command)
    {
        // Exercise real Windows job/process handles with an isolated helper, never the packaged app.
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-Command");
        info.ArgumentList.Add(command);
        return Process.Start(info)!;
    }
}
