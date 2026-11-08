<#
.SYNOPSIS
Gets (or renews) this PC's Tailscale HTTPS certificate and hands it to the A2Core server in WSL.

.DESCRIPTION
Runs `tailscale cert` for this machine's MagicDNS name (<machine>.<tailnet>.ts.net; tailnet admin console > DNS:
MagicDNS and HTTPS Certificates must be on). The files go to %LocalAppData%\A2Core\tls (cert.pem, key.pem), which only
your Windows user can read. `tailscale cert` returns the current certificate while it is still valid for a while and a
new one (Let's Encrypt, 90 days) when it is due, so running this weekly keeps it fresh.

When the server is installed in WSL (/etc/aion/tls exists) and the certificate differs from the installed one, it is
copied to /etc/aion/tls (root:aion 640, as install.sh does) and aion-host is restarted (the host reads the certificate
at startup; players online are disconnected for about a minute). -NoRestart only copies.

-Register adds a weekly scheduled task (Mondays 05:00, runs when you are logged on; missed runs start later) that runs
this script with the same -HostName and -Distro.

.EXAMPLE
deploy\windows\renew-cert.ps1
deploy\windows\renew-cert.ps1 -Register
deploy\windows\renew-cert.ps1 -HostName mypc.tail1234.ts.net -WhatIf
#>
#Requires -Version 5.1
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$HostName,
    [string]$Distro = "Ubuntu-24.04",
    [string]$OutDir = (Join-Path $env:LOCALAPPDATA "A2Core\tls"),
    [switch]$NoRestart,
    [switch]$Register
)
$ErrorActionPreference = "Stop"
Set-StrictMode -Version 3
$env:WSL_UTF8 = "1"
$TaskName = "A2Core certificate renewal"

function Invoke-Native {
    param([string]$File, [string[]]$Arguments)
    $ErrorActionPreference = "Continue"
    $output = & $File @Arguments 2>&1 | ForEach-Object { ("$_" -replace "`0", "") }
    return [pscustomobject]@{ Code = $LASTEXITCODE; Output = @($output) }
}

# C:\Users\me\AppData\... -> /mnt/c/Users/me/AppData/... (WSL's default automount root).
function ConvertTo-WslPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if ($full -notmatch '^([A-Za-z]):\\(.*)$') { throw "Not a drive path: $full" }
    return "/mnt/" + $Matches[1].ToLowerInvariant() + "/" + ($Matches[2] -replace '\\', '/')
}

$tailscale = Get-Command tailscale.exe -ErrorAction SilentlyContinue
$tailscalePath = if ($tailscale) { $tailscale.Source } else { Join-Path $env:ProgramFiles "Tailscale\tailscale.exe" }
if (-not (Test-Path -LiteralPath $tailscalePath)) { throw "tailscale.exe not found; install Tailscale for Windows." }

if (-not $HostName) {
    $status = Invoke-Native $tailscalePath @("status", "--json")
    if ($status.Code -ne 0) { throw "tailscale status failed: $($status.Output -join ' ')" }
    $HostName = (($status.Output -join "`n") | ConvertFrom-Json).Self.DNSName.TrimEnd(".")
    if (-not $HostName) { throw "No MagicDNS name for this machine; turn on MagicDNS or pass -HostName." }
}
Write-Host "Certificate for $HostName"

if ($Register) {
    $argument = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$PSCommandPath`" -HostName $HostName -Distro $Distro"
    $action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument $argument
    $trigger = New-ScheduledTaskTrigger -Weekly -DaysOfWeek Monday -At "05:00"
    $principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    if ($PSCmdlet.ShouldProcess($TaskName, "register weekly scheduled task")) {
        Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings `
            -Description "Renews the A2Core server's Tailscale certificate (deploy/windows/renew-cert.ps1)." -Force | Out-Null
        Write-Host "Registered '$TaskName' (Mondays 05:00)."
    }
}

$cert = Join-Path $OutDir "cert.pem"
$key = Join-Path $OutDir "key.pem"
if ($PSCmdlet.ShouldProcess($OutDir, "tailscale cert $HostName")) {
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    $result = Invoke-Native $tailscalePath @("cert", "--cert-file", $cert, "--key-file", $key, $HostName)
    $result.Output | ForEach-Object { Write-Host "   $_" }
    if ($result.Code -ne 0) { throw "tailscale cert failed ($($result.Code)). Is HTTPS on in the tailnet's DNS settings?" }
}
$wslCert = ConvertTo-WslPath $cert
$wslKey = ConvertTo-WslPath $key
Write-Host "WSL paths: --certificate $wslCert --key $wslKey"

$installed = Invoke-Native "wsl.exe" @("-d", $Distro, "-u", "root", "--exec", "test", "-d", "/etc/aion/tls")
if ($installed.Code -ne 0) {
    Write-Host "The server is not installed in $Distro yet; pass the paths above to deploy/windows/wsl-install.sh."
    return
}
if (-not (Test-Path -LiteralPath $cert)) { return }   # -WhatIf on a first run
$windowsHash = (Get-FileHash -LiteralPath $cert -Algorithm SHA256).Hash.ToLowerInvariant()
$current = Invoke-Native "wsl.exe" @("-d", $Distro, "-u", "root", "--exec", "sha256sum", "/etc/aion/tls/cert.pem")
$installedHash = if ($current.Code -eq 0) { ($current.Output -join "").Split(" ")[0] } else { "" }
if ($installedHash -eq $windowsHash) {
    Write-Host "The server already has this certificate."
    return
}
if ($PSCmdlet.ShouldProcess("$Distro /etc/aion/tls", "install the new certificate and restart aion-host")) {
    foreach ($pair in @(@($wslCert, "/etc/aion/tls/cert.pem"), @($wslKey, "/etc/aion/tls/key.pem"))) {
        $copy = Invoke-Native "wsl.exe" @("-d", $Distro, "-u", "root", "--exec", "install", "-o", "root", "-g", "aion", "-m", "640", $pair[0], $pair[1])
        if ($copy.Code -ne 0) { throw "Copy to $($pair[1]) failed: $($copy.Output -join ' ')" }
    }
    Write-Host "Installed the new certificate."
    if (-not $NoRestart) {
        $restart = Invoke-Native "wsl.exe" @("-d", $Distro, "-u", "root", "--exec", "systemctl", "try-restart", "aion-host")
        if ($restart.Code -ne 0) { throw "systemctl try-restart aion-host failed: $($restart.Output -join ' ')" }
        Write-Host "aion-host restarted."
    }
}
