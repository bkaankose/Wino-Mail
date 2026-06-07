using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Wino.Core.Authentication;
using Xunit;

namespace Wino.Core.Tests.Authentication;

public sealed class LoopbackRedirectListenerTests
{
    [Fact]
    public async Task WaitForRedirect_CapturesCodeAndState()
    {
        using var listener = new LoopbackRedirectListener(0);
        listener.Start();
        var port = listener.Port;

        var waitTask = listener.WaitForRedirectAsync(CancellationToken.None);

        using var http = new HttpClient();
        _ = await http.GetAsync($"http://localhost:{port}/?code=ABC123&state=XYZ789");

        var result = await waitTask;

        result.Code.Should().Be("ABC123");
        result.State.Should().Be("XYZ789");
        result.Error.Should().BeNull();
    }

    [Fact]
    public async Task WaitForRedirect_UrlDecodesValuesAndSurfacesError()
    {
        using var listener = new LoopbackRedirectListener(0);
        listener.Start();
        var port = listener.Port;

        var waitTask = listener.WaitForRedirectAsync(CancellationToken.None);

        using var http = new HttpClient();
        _ = await http.GetAsync($"http://localhost:{port}/?error=access_denied&state=a%2Bb");

        var result = await waitTask;

        result.Code.Should().BeNull();
        result.Error.Should().Be("access_denied");
        result.State.Should().Be("a+b");
    }

    [Fact]
    public async Task WaitForRedirect_HonorsCancellation()
    {
        using var listener = new LoopbackRedirectListener(0);
        listener.Start();

        using var cts = new CancellationTokenSource();
        var waitTask = listener.WaitForRedirectAsync(cts.Token);
        cts.Cancel();

        var act = async () => await waitTask;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
