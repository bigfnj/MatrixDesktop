using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MatrixDesktop;

internal static class HostControlLifetime
{
    internal static void DetachAndDispose(Control control, Control.ControlCollection owner, Action<Exception> report)
    {
        owner.Remove(control);
        try { control.Dispose(); }
        catch (Exception error) when (error is InvalidOperationException or COMException) { report(error); }
    }
}
