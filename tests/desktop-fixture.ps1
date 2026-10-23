[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ReadyFile,
    [Parameter(Mandatory = $true)][string]$StopFile
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# This test helper writes only its own readiness marker beneath the test artifact root.
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\artifacts\desktop-snapshot')) + [IO.Path]::DirectorySeparatorChar
foreach ($candidate in @($ReadyFile, $StopFile)) {
    if (-not [IO.Path]::GetFullPath($candidate).StartsWith($artifactRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Fixture markers must remain inside artifacts\desktop-snapshot.'
    }
}
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class DesktopFixtureNative {
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("dwmapi.dll")] public static extern int DwmFlush();
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
'@
[void][DesktopFixtureNative]::SetProcessDpiAwarenessContext([IntPtr](-4))
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Windows.Forms, System.Drawing @'
using System;
using System.Drawing;
using System.Windows.Forms;
public sealed class MatrixDesktopFixture : Form {
    public MatrixDesktopFixture() {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.None;
        Bounds = SystemInformation.VirtualScreen;
        Text = "MatrixDesktop synthetic snapshot fixture";
    }
    protected override void OnPaint(PaintEventArgs e) {
        Color[] colors = { Color.FromArgb(32, 64, 224), Color.FromArgb(224, 48, 64),
                           Color.FromArgb(32, 208, 96), Color.FromArgb(208, 64, 208) };
        for (int y = 0; y < ClientSize.Height; y += 64)
            for (int x = 0; x < ClientSize.Width; x += 64)
                using (var brush = new SolidBrush(colors[(x / 64 + 2 * (y / 64)) % 4]))
                    e.Graphics.FillRectangle(brush, x, y, 64, 64);
        e.Graphics.Flush();
    }
}
'@
$form = New-Object MatrixDesktopFixture
$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 200
$started = [DateTime]::UtcNow
$script:ready = $false
$timer.Add_Tick({
    if ((Test-Path -LiteralPath $StopFile) -or ([DateTime]::UtcNow - $started).TotalSeconds -gt 120) {
        $timer.Stop()
        $form.Close()
        return
    }
    if (-not $script:ready) {
        [void][DesktopFixtureNative]::SetWindowPos($form.Handle, [IntPtr](-1), 0, 0, 0, 0, 0x13)
        $form.Refresh()
        [void][DesktopFixtureNative]::DwmFlush()
        $bounds = $form.Bounds
        $payload = @{
            pid = $PID
            hwnd = $form.Handle.ToInt64()
            x = $bounds.X
            y = $bounds.Y
            width = $bounds.Width
            height = $bounds.Height
            powershell = $PSVersionTable.PSVersion.ToString()
        } | ConvertTo-Json -Compress
        $temporaryMarker = $ReadyFile + '.tmp'
        [IO.File]::WriteAllText($temporaryMarker, $payload, [Text.Encoding]::UTF8)
        [IO.File]::Move($temporaryMarker, $ReadyFile)
        $script:ready = $true
    }
})
$form.Add_Shown({ $form.Activate(); $form.BringToFront(); $timer.Start() })
try { [System.Windows.Forms.Application]::Run($form) }
finally { $timer.Dispose(); $form.Dispose() }
