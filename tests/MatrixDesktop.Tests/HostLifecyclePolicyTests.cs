using System.Text.RegularExpressions;

namespace MatrixDesktop.Tests;

// Host startup, storage and lifecycle decisions that the WinForms host delegates to pure code.
internal static class HostLifecyclePolicyTests
{
    internal static IEnumerable<(string, Action)> All =>
    [
        ("Host WebView2 profile record keeps the format the gate parses", ProfileRecordFormat),
    ];

    private static void ProfileRecordFormat()
    {
        const string requested = @"C:\Users\Some One\AppData\Local\MatrixDesktop\WebView2";
        const string effective = @"D:\gate run\artifacts\gate\webview-profile";
        var record = HostStorage.ProfileRecord(requested, effective);
        // The gate-ci contract: 'WebView2 profile requested=<path> effective=<path>'.
        var match = Regex.Match(record, "^WebView2 profile requested=(?<requested>.*) effective=(?<effective>.*)$");
        Check.True(match.Success, $"the record must match the gate's pattern: {record}");
        Check.Equal(requested, match.Groups["requested"].Value, "the requested folder must round-trip, spaces included");
        Check.Equal(effective, match.Groups["effective"].Value, "the effective folder must round-trip, spaces included");
    }
}
