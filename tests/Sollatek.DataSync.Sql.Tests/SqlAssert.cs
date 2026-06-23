namespace Sollatek.DataSync.Tests;

internal static class SqlAssert
{
    public static void Equal(string expected, string actual)
    {
        Assert.Equal(NormalizeLineEndings(expected), NormalizeLineEndings(actual));
    }

    private static string NormalizeLineEndings(string value)
    {
        return value.ReplaceLineEndings("\n");
    }
}
