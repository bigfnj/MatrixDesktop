namespace MatrixDesktop;

internal static class DesktopHostDiscovery
{
    internal static nint Ensure(Func<nint> locate, Action<nuint, nint> request)
    {
        var existing = locate();
        if (existing != 0) return existing;
        request(0xD, 1);
        existing = locate();
        if (existing != 0) return existing;
        request(0, 0);
        return locate();
    }
}
