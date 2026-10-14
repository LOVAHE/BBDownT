using System.Net.Sockets;
using BBDownT.Core.Util;

namespace BBDownT.Tests;

public class SubUtilTests
{
    [Fact]
    public async Task IdentityRotation_LeavesExhaustedTransientFailuresToThePageRetry()
    {
        var attempts = 0;
        var waits = 0;
        var failure = new HttpRequestException("connection refused", new SocketException((int)SocketError.ConnectionRefused));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => SubUtil.WithIdentityRotationAsync<string>(
            "https://subtitle.example/track.json",
            () =>
            {
                attempts++;
                throw failure;
            },
            _ =>
            {
                waits++;
                return Task.CompletedTask;
            }));

        Assert.Equal(1, attempts);
        Assert.Equal(0, waits);
        Assert.Same(failure, error);
    }
}
