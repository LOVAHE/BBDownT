using BBDownT.Core.Util;

namespace BBDownT.Tests;

public class BilibiliBvConverterTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(170001)]
    [InlineData(455017605)]
    [InlineData(2251799813685247)]
    public void EncodeAndDecode_RoundTripsSupportedAid(long aid)
    {
        var bvid = BilibiliBvConverter.Encode(aid);

        Assert.StartsWith("BV1", bvid);
        Assert.Equal(aid, BilibiliBvConverter.Decode(bvid[3..]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2251799813685248)]
    public void Encode_RejectsUnsupportedAid(long aid)
    {
        Assert.Throws<Exception>(() => BilibiliBvConverter.Encode(aid));
    }
}
