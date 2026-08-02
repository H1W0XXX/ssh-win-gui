#requires -Version 7.4
#requires -PSEdition Core

param(
    [Parameter(Mandatory)] [string] $Worker,
    [Parameter(Mandatory)] [string] $HostName,
    [Parameter(Mandatory)] [int] $Port,
    [Parameter(Mandatory)] [string] $UserName,
    [Parameter(Mandatory)] [string] $PrivateKeyPath,
    [Parameter(Mandatory)] [string] $RemoteRoot,
    [Parameter(Mandatory)] [string] $LocalSource,
    [Parameter(Mandatory)] [string] $LocalOutput,
    [string] $JumpHostName,
    [int] $JumpPort = 22,
    [string] $JumpUserName,
    [string] $JumpPrivateKeyPath,
    [switch] $Compress
)

$ErrorActionPreference = "Stop"

function Invoke-WorkerTransfer {
    param(
        [string] $Name,
        [string] $Direction,
        [string] $LocalPath,
        [string] $RemotePath,
        [bool] $CopyContents
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new($Worker)
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        throw "Unable to start rsync worker."
    }

    $hello = $process.StandardOutput.ReadLine() | ConvertFrom-Json
    if ($hello.type -ne "hello") {
        throw "Worker did not send its hello record."
    }

    $remoteEndpoint = @{
        host = $HostName
        port = $Port
        user = $UserName
        auth = @{
            method = "private_key"
            privateKeyPath = $PrivateKeyPath
        }
        hostKey = @{ mode = "log_only" }
    }
    if ($JumpHostName) {
        $remoteEndpoint.proxy = @{
            type = "jump"
            jump = @{
                host = $JumpHostName
                port = $JumpPort
                user = $JumpUserName
                auth = @{
                    method = "private_key"
                    privateKeyPath = $JumpPrivateKeyPath
                }
                hostKey = @{ mode = "log_only" }
            }
        }
    }

    $request = @{
        type = "transfer"
        requestId = "smoke-$Name"
        transfer = @{
            direction = $Direction
            localPath = $LocalPath
            remotePath = $RemotePath
            copyContents = $CopyContents
            remote = $remoteEndpoint
            options = @{
                preserveTimes = $true
                preservePermissions = $false
                preserveLinks = $false
                compress = [bool]$Compress
            }
        }
    } | ConvertTo-Json -Compress -Depth 12

    $process.StandardInput.WriteLine($request)
    $process.StandardInput.Flush()
    $events = [Collections.Generic.List[object]]::new()
    $success = $false
    while ($true) {
        $line = $process.StandardOutput.ReadLine()
        if ($null -eq $line) {
            break
        }
        $message = $line | ConvertFrom-Json
        $events.Add($message)
        if ($message.type -eq "completed") {
            $success = $message.state -eq "success"
            break
        }
    }

    $process.StandardInput.Close()
    if (-not $process.WaitForExit(10000)) {
        $process.Kill()
        throw "$Name worker did not exit."
    }
    $stderr = $process.StandardError.ReadToEnd().Trim()
    $diagnostics = $events |
        Where-Object { $_.type -in @("log", "error", "completed") } |
        ForEach-Object {
            if ($_.message) { $_.message }
            elseif ($_.error) { $_.error.message }
            else { $_.state }
        }

    [pscustomobject]@{
        name = $Name
        success = $success
        exitCode = $process.ExitCode
        stderr = $stderr
        diagnostics = @($diagnostics)
    }
}

$source = (Resolve-Path -LiteralPath $LocalSource).Path
$output = (Resolve-Path -LiteralPath $LocalOutput).Path
$remote = $RemoteRoot.TrimEnd('/')

$results = @(
    Invoke-WorkerTransfer "upload-file" "upload" (Join-Path $source "single.txt") "$remote/upload-file/" $false
    Invoke-WorkerTransfer "upload-folder" "upload" (Join-Path $source "folder") "$remote/upload-folder/" $false
    Invoke-WorkerTransfer "download-file" "download" ((Join-Path $output "download-file") + [IO.Path]::DirectorySeparatorChar) "$remote/download-source/remote-single.txt" $false
    Invoke-WorkerTransfer "download-folder" "download" ((Join-Path $output "download-folder") + [IO.Path]::DirectorySeparatorChar) "$remote/download-source/folder" $false
)

$results | ConvertTo-Json -Depth 8
if ($results.success -contains $false) {
    exit 1
}
