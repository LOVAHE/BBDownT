namespace BBDownT.Core.Util;

internal static class PathSegmentSanitizer
{
    private static readonly char[] UnsafeChars = Path.GetInvalidFileNameChars()
        .Concat(['/', '\\'])
        .Distinct()
        .ToArray();

    public static string Sanitize(string value)
    {
        foreach (var character in UnsafeChars)
        {
            value = value.Replace(character, '_');
        }

        return value.Trim().TrimEnd('.');
    }
}
