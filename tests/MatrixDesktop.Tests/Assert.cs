namespace MatrixDesktop.Tests;

// Hand-rolled assertions. Every one takes a message explaining what the invariant means,
// so a failure says why it matters rather than only what the values were.
internal static class Check
{
    public static void True(bool value, string because)
    {
        if (!value) throw new InvalidOperationException($"expected true. {because}");
    }

    public static void False(bool value, string because)
    {
        if (value) throw new InvalidOperationException($"expected false. {because}");
    }

    public static void Equal<T>(T expected, T actual, string because) where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"expected '{expected}', got '{actual}'. {because}");
        }
    }

    public static void Close(double expected, double actual, double tolerance, string because)
    {
        if (Math.Abs(expected - actual) > tolerance)
        {
            throw new InvalidOperationException(
                $"expected {expected} within {tolerance}, got {actual} (delta {Math.Abs(expected - actual)}). {because}");
        }
    }

    public static void Contains(string haystack, string needle, string because)
    {
        if (!haystack.Contains(needle, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"expected to find '{needle}' in '{haystack}'. {because}");
        }
    }

    public static void DoesNotContain(string haystack, string needle, string because)
    {
        if (haystack.Contains(needle, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"did not expect '{needle}' in '{haystack}'. {because}");
        }
    }

    public static void SetEqual(IEnumerable<string> expected, IEnumerable<string> actual, string because)
    {
        var e = new SortedSet<string>(expected, StringComparer.OrdinalIgnoreCase);
        var a = new SortedSet<string>(actual, StringComparer.OrdinalIgnoreCase);
        var missing = e.Except(a, StringComparer.OrdinalIgnoreCase).ToArray();
        var extra = a.Except(e, StringComparer.OrdinalIgnoreCase).ToArray();
        if (missing.Length > 0 || extra.Length > 0)
        {
            throw new InvalidOperationException(
                $"set mismatch. missing=[{string.Join(", ", missing)}] unexpected=[{string.Join(", ", extra)}]. {because}");
        }
    }
}
