#requires -Version 7.4
#requires -PSEdition Core

[CmdletBinding()]
param(
    [string]$HostName = "82.157.129.23",
    [int]$Port = 22,
    [string]$UserName = "ubuntu",
    [string]$PrivateKeyPath = "D:\北京\id_ecdsa",
    [string]$ApplicationPath = (Join-Path $PSScriptRoot "..\src\ssh-win-gui\bin\Release\net8.0-windows10.0.19041.0\win-x64\ssh-win-gui.exe")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;

public static class TerminalNavigationSmokeNative
{
    public delegate bool EnumProc(IntPtr hwnd, IntPtr lparam);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr lparam);
    [DllImport("user32.dll")] public static extern int GetClassName(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr processId);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint source, uint target, bool attach);
    [DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr GetFocus();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extra);
}
'@

function Invoke-RemoteCommand {
    param([Parameter(Mandatory)] [string]$Command)
    $output = & ssh.exe -o BatchMode=yes -o StrictHostKeyChecking=no -o UserKnownHostsFile=NUL `
        -i $PrivateKeyPath -p $Port "$UserName@$HostName" $Command 2>$null
    if ($LASTEXITCODE -ne 0) { throw "Remote command failed: $Command" }
    return ($output | Out-String).Trim()
}

function Send-TerminalCharacters {
    param([Parameter(Mandatory)] [IntPtr]$Hwnd, [Parameter(Mandatory)] [string]$Text)
    foreach ($character in $Text.ToCharArray()) {
        [TerminalNavigationSmokeNative]::PostMessage($Hwnd, 0x0102, [IntPtr][int]$character, [IntPtr]1) | Out-Null
    }
}

function Find-TerminalWindow {
    param([Parameter(Mandatory)] [IntPtr]$MainWindow)
    $script:terminalWindow = [IntPtr]::Zero
    $callback = [TerminalNavigationSmokeNative+EnumProc]{
        param($hwnd, $lparam)
        $className = [Text.StringBuilder]::new(128)
        [TerminalNavigationSmokeNative]::GetClassName($hwnd, $className, $className.Capacity) | Out-Null
        if ([TerminalNavigationSmokeNative]::IsWindowVisible($hwnd) -and
            $className.ToString() -eq "HwndTerminalClass") {
            $script:terminalWindow = $hwnd
        }
        return $true
    }
    [TerminalNavigationSmokeNative]::EnumChildWindows($MainWindow, $callback, [IntPtr]::Zero) | Out-Null
    return $script:terminalWindow
}

function New-MouseLParam {
    param([int]$X, [int]$Y)
    return [IntPtr](($X -band 0xffff) -bor (($Y -band 0xffff) -shl 16))
}

$marker = "/tmp/ssh-win-gui-nav-$([Guid]::NewGuid().ToString('N'))"
$pasteMarker = "/tmp/ssh-win-gui-paste-$([Guid]::NewGuid().ToString('N'))"
$tmuxSession = "ssh-win-gui-smoke-$([Guid]::NewGuid().ToString('N').Substring(0, 12))"
$process = $null
$previousDiagnostics = $env:SSH_WIN_GUI_INPUT_DIAGNOSTICS
$env:SSH_WIN_GUI_INPUT_DIAGNOSTICS = "1"
try {
    Invoke-RemoteCommand -Command "rm -f -- '$marker'" | Out-Null
    $resolvedApplication = (Resolve-Path -LiteralPath $ApplicationPath).Path
    $process = Start-Process -FilePath $resolvedApplication `
        -ArgumentList @("$UserName@$HostName`:$Port") `
        -WorkingDirectory (Split-Path -Parent $resolvedApplication) -PassThru
    if (-not $process.WaitForInputIdle(15000)) { throw "Application did not become input-idle." }
    $process.Refresh()

    $deadline = (Get-Date).AddSeconds(20)
    do {
        $terminal = Find-TerminalWindow -MainWindow $process.MainWindowHandle
        if ($terminal -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 200 }
    } while ($terminal -eq [IntPtr]::Zero -and (Get-Date) -lt $deadline)
    if ($terminal -eq [IntPtr]::Zero) { throw "Native SSH terminal did not appear." }

    # Regression for the 64-bit wParam overflow that previously escaped from
    # OnThreadPreprocessMessage and terminated the entire application.
    $wideWParam = [IntPtr]::new([long]0x0000000100000000)
    [TerminalNavigationSmokeNative]::PostMessage($terminal, 0x0000, $wideWParam, [IntPtr]::Zero) | Out-Null
    Start-Sleep -Milliseconds 250
    $process.Refresh()
    if ($process.HasExited) { throw "Application exited after a non-keyboard message with a wide wParam." }
    Write-Output "WIDE_WPARAM_REGRESSION=PASS"

    # The native HWND is created before SSH authentication and the remote shell
    # have necessarily finished. Give the live shell time to become writable.
    Start-Sleep -Milliseconds 3000

    $increment = "n=`$(cat '$marker' 2>/dev/null || echo 0); echo `$((n+1)) > '$marker'"
    Send-TerminalCharacters -Hwnd $terminal -Text $increment
    Send-TerminalCharacters -Hwnd $terminal -Text "`r"
    $deadline = (Get-Date).AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 250
        $first = Invoke-RemoteCommand -Command "cat '$marker' 2>/dev/null || true"
    } while ($first -ne "1" -and (Get-Date) -lt $deadline)
    if ($first -ne "1") { throw "Initial command did not execute; counter='$first'." }

    [TerminalNavigationSmokeNative]::SetForegroundWindow($process.MainWindowHandle) | Out-Null
    $rect = [TerminalNavigationSmokeNative+RECT]::new()
    if (-not [TerminalNavigationSmokeNative]::GetWindowRect($terminal, [ref]$rect)) { throw "Unable to locate terminal window." }
    [TerminalNavigationSmokeNative]::SetCursorPos([int](($rect.Left + $rect.Right) / 2), [int](($rect.Top + $rect.Bottom) / 2)) | Out-Null
    [TerminalNavigationSmokeNative]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    [TerminalNavigationSmokeNative]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 400
    $currentThread = [TerminalNavigationSmokeNative]::GetCurrentThreadId()
    $terminalThread = [TerminalNavigationSmokeNative]::GetWindowThreadProcessId($terminal, [IntPtr]::Zero)
    if (-not [TerminalNavigationSmokeNative]::AttachThreadInput($currentThread, $terminalThread, $true)) {
        throw "Unable to attach to terminal input thread."
    }
    try {
        [TerminalNavigationSmokeNative]::SetForegroundWindow($process.MainWindowHandle) | Out-Null
        [TerminalNavigationSmokeNative]::SetFocus($terminal) | Out-Null
        $focused = [TerminalNavigationSmokeNative]::GetFocus()
        Write-Output "TERMINAL_HWND=$terminal"
        Write-Output "FOCUSED_HWND=$focused"
        if ($focused -ne $terminal) { throw "Terminal did not receive keyboard focus." }
        [TerminalNavigationSmokeNative]::PostMessage($terminal, 0x0100, [IntPtr]0x26, [IntPtr]0x01480001) | Out-Null
        [TerminalNavigationSmokeNative]::PostMessage($terminal, 0x0101, [IntPtr]0x26, [IntPtr]0xC1480001) | Out-Null
        [TerminalNavigationSmokeNative]::PostMessage($terminal, 0x0102, [IntPtr]13, [IntPtr]0x001C0001) | Out-Null
        Start-Sleep -Milliseconds 500
    }
    finally {
        [TerminalNavigationSmokeNative]::AttachThreadInput($currentThread, $terminalThread, $false) | Out-Null
    }

    $deadline = (Get-Date).AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 250
        $second = Invoke-RemoteCommand -Command "cat '$marker' 2>/dev/null || true"
    } while ($second -ne "2" -and (Get-Date) -lt $deadline)
    Write-Output "INITIAL_COMMAND_COUNT=$first"
    Write-Output "AFTER_UP_AND_ENTER_COUNT=$second"
    if ($second -ne "2") { throw "Up arrow did not recall and rerun the previous command." }
    Write-Output "TERMINAL_NAVIGATION_SMOKE=PASS"

    $client = [TerminalNavigationSmokeNative+RECT]::new()
    if (-not [TerminalNavigationSmokeNative]::GetClientRect($terminal, [ref]$client)) {
        throw "Unable to read the terminal client rectangle."
    }
    Set-Clipboard -Value "printf 1 > '$pasteMarker'"
    $center = New-MouseLParam -X ([int]($client.Right / 2)) -Y ([int]($client.Bottom / 2))
    [TerminalNavigationSmokeNative]::SendMessage($terminal, 0x0207, [IntPtr]::Zero, $center) | Out-Null
    [TerminalNavigationSmokeNative]::SendMessage($terminal, 0x0208, [IntPtr]::Zero, $center) | Out-Null
    [TerminalNavigationSmokeNative]::SendMessage($terminal, 0x0102, [IntPtr]13, [IntPtr]0x001C0001) | Out-Null
    $deadline = (Get-Date).AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 250
        $pasted = Invoke-RemoteCommand -Command "cat '$pasteMarker' 2>/dev/null || true"
    } while ($pasted -ne "1" -and (Get-Date) -lt $deadline)
    Write-Output "SHELL_MIDDLE_PASTE_RESULT=$pasted"
    if ($pasted -ne "1") { throw "Middle-button clipboard paste did not execute in the regular shell." }
    Write-Output "TERMINAL_MOUSE_SMOKE=PASS"

    $copyToken = "SSH_WIN_GUI_COPY_$([Guid]::NewGuid().ToString('N'))"
    Send-TerminalCharacters -Hwnd $terminal -Text "tmux new-session -s '$tmuxSession'`r"
    Start-Sleep -Milliseconds 1500
    Send-TerminalCharacters -Hwnd $terminal -Text "tmux set -g mouse on; clear; printf '$copyToken\n'`r"
    Start-Sleep -Milliseconds 1000

    Set-Clipboard -Value ""
    $selectionEndX = $client.Right - 8
    [TerminalNavigationSmokeNative]::keybd_event(0x10, 0x2A, 0, [UIntPtr]::Zero)
    try {
        [TerminalNavigationSmokeNative]::SendMessage($terminal, 0x0201, [IntPtr]0x0004, (New-MouseLParam -X 5 -Y 10)) | Out-Null
        [TerminalNavigationSmokeNative]::SendMessage($terminal, 0x0200, [IntPtr]0x0005, (New-MouseLParam -X $selectionEndX -Y 10)) | Out-Null
        [TerminalNavigationSmokeNative]::SendMessage($terminal, 0x0202, [IntPtr]0x0004, (New-MouseLParam -X $selectionEndX -Y 10)) | Out-Null
    }
    finally {
        [TerminalNavigationSmokeNative]::keybd_event(0x10, 0x2A, 0x0002, [UIntPtr]::Zero)
    }
    Start-Sleep -Milliseconds 500
    $copied = Get-Clipboard -Raw
    Write-Output "TMUX_SHIFT_COPY_LENGTH=$($copied.Length)"
    Write-Output "TMUX_SHIFT_COPY_TEXT=$(($copied -replace "`r", '<CR>' -replace "`n", '<LF>'))"
    Write-Output "TMUX_SHIFT_COPY_CONTAINS_TOKEN=$($copied.Contains($copyToken))"
    if (-not $copied.Contains($copyToken)) {
        throw "Shift+left-drag did not copy the selected tmux text to the clipboard."
    }

}
finally {
    try {
        Invoke-RemoteCommand -Command "tmux kill-session -t '$tmuxSession' 2>/dev/null || true; rm -f -- '$marker' '$pasteMarker'" | Out-Null
        Write-Output "REMOTE_MARKERS_REMOVED=$marker,$pasteMarker"
    } catch { Write-Warning "Failed to remove exact remote marker: $marker" }
    if ($null -ne $process -and -not $process.HasExited) {
        $process.CloseMainWindow() | Out-Null
        if (-not $process.WaitForExit(10000)) { Write-Warning "Test process remains running: $($process.Id)" }
    }
    $env:SSH_WIN_GUI_INPUT_DIAGNOSTICS = $previousDiagnostics
}
