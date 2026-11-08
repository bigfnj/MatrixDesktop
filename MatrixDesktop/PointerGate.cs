namespace MatrixDesktop;

// Which low-level mouse events reach the page. The global hook sees every application's
// input, so the 30 ms move throttle runs before the desktop-foreground probe, and it
// advances whether or not the desktop is in front: a move over another application
// costs a clock comparison, not a window-class read.
internal sealed class PointerGate(Func<bool> desktopHasForeground)
{
    private const int MouseMove = 0x0200;
    private const long MoveIntervalMs = 30;
    private long _lastMove;

    internal bool ShouldForward(int message, long now)
    {
        if (message == MouseMove)
        {
            if (now - _lastMove < MoveIntervalMs) return false;
            _lastMove = now;
            return desktopHasForeground();
        }
        if (!desktopHasForeground()) return false;
        // A forwarded button or wheel event restarts the move interval, as it always has.
        _lastMove = now;
        return true;
    }
}
