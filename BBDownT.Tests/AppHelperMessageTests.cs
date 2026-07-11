using BBDownT.Core;

namespace BBDownT.Tests;

public class AppHelperMessageTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1024)]
    public void PackAndReadMessage_RoundTripsPayload(int length)
    {
        var payload = Enumerable.Range(0, length).Select(index => (byte)(index % 251)).ToArray();

        var decoded = AppHelper.ReadMessage(AppHelper.PackMessage(payload));

        Assert.Equal(payload, decoded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void ReadMessage_RejectsTruncatedHeader(int length)
    {
        Assert.Throws<InvalidDataException>(() => AppHelper.ReadMessage(new byte[length]));
    }

    [Fact]
    public void ReadMessage_RejectsDeclaredPayloadLargerThanAvailableData()
    {
        var frame = new byte[] { 0, 0, 0, 0, 10, 1, 2 };

        Assert.Throws<InvalidDataException>(() => AppHelper.ReadMessage(frame));
    }
}
