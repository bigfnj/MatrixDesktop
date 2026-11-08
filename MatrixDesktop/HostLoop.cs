using System.Windows.Forms;

namespace MatrixDesktop;

// Application.Run subscribes to the context's ThreadExit only when it starts. A context that
// ends itself while it is being constructed (a screensaver preview whose parent is already
// gone, a renderer that cannot be created) has raised ThreadExit to nobody and disposed the
// timer and window that could end the loop, so Run would pump forever with no window: one
// invisible process per occurrence. Such a context is disposed instead of run.
internal static class HostLoop
{
    internal static void Run(ApplicationContext context, Func<bool> exitRequested)
    {
        if (exitRequested())
        {
            context.Dispose();
            return;
        }
        Application.Run(context);
    }
}
