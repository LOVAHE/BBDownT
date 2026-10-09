using System.Globalization;

namespace BBDownT.Core.Util;

internal static class PublishTime
{
    private static readonly TimeSpan BeijingOffset = TimeSpan.FromHours(8);

    internal static long Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)
            || !DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value))
            return 0;
        if (value.Kind == DateTimeKind.Unspecified)
            return new DateTimeOffset(value, BeijingOffset).ToUnixTimeSeconds();
        return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture).ToUnixTimeSeconds();
    }
}
