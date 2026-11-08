<#
.SYNOPSIS
Prepares Windows 11 to host the A2Core server in WSL2 Ubuntu 24.04 (deploy/windows/README.md, path A).

.DESCRIPTION
Run in an elevated PowerShell (Run as administrator) as the Windows user who will own the server. Safe to run again:
each step checks first and changes only what is missing. -WhatIf shows the changes without making them.

1. Checks Windows 11 22H2 or later (mirrored networking needs build 22621+).
2. Enables WSL (wsl --install --no-distribution) when it is missing; Windows then needs a restart, run this again.
3. Installs the Ubuntu-24.04 distribution when it is missing (it asks for a Linux user name and password).
4. Merges into %UserProfile%\.wslconfig: [wsl2] networkingMode=mirrored and [experimental] hostAddressLoopback=true
   (the Windows game client reaches the server on this PC's Tailscale address). Other settings stay; the old file is
   copied to .wslconfig.bak-<time> first.
5. Enables systemd in Ubuntu-24.04's /etc/wsl.conf ([boot] systemd=true), with a backup of the old file.
Optional:
   -ConfigureFirewall     inbound rules for the server's TCP ports (443, 3700, 8087, 8091, 8780, 13328), only from
                          Tailscale addresses (100.64.0.0/10): a Windows Defender Firewall rule and a Hyper-V firewall
                          rule for WSL.
   -RegisterStartupTask   a scheduled task that keeps Ubuntu-24.04 running (and so its systemd services) from logon;
                          with -AtStartup from boot, before anyone logs on (UNVERIFIED with WSL, see README).
Other distributions are never touched. Nothing is deleted.

.EXAMPLE
deploy\windows\setup-wsl.ps1 -WhatIf
deploy\windows\setup-wsl.ps1
deploy\windows\setup-wsl.ps1 -ConfigureFirewall -RegisterStartupTask
#>
#Requires -Version 5.1
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$Distro = "Ubuntu-24.04",
    [switch]$ConfigureFirewall,
    [switch]$RegisterStartupTask,
    [switch]$AtStartup
)
$ErrorActionPreference = "Stop"
Set-StrictMode -Version 3
$Ports = @("443", "3700", "8087", "8091", "8780", "13328")
$TailnetRange = "100.64.0.0/10"
$WslVmCreatorId = "{40E0AC32-46A5-438A-A0B2-2B479E8F2E90}"
$TaskName = "A2Core WSL keepalive"
$env:WSL_UTF8 = "1"   # wsl.exe prints UTF-16 otherwise

function Step([string]$text) { Write-Host ""; Write-Host "== $text" -ForegroundColor Cyan }
function Note([string]$text) { Write-Host "   $text" }

# Native programs: stderr must not become a terminating error (Windows PowerShell 5.1); NULs from UTF-16 removed.
function Invoke-Native {
    param([string]$File, [string[]]$Arguments)
    $ErrorActionPreference = "Continue"
    $output = & $File @Arguments 2>&1 | ForEach-Object { ("$_" -replace "`0", "") }
    return [pscustomobject]@{ Code = $LASTEXITCODE; Output = @($output) }
}

function Get-Distros {
    $result = Invoke-Native "wsl.exe" @("--list", "--quiet")
    if ($result.Code -ne 0) { return @() }
    return @($result.Output | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

# Bash script run as root in the distribution; base64 keeps quoting and line endings intact through wsl.exe.
function Invoke-InDistro([string]$Script) {
    $bytes = [Text.Encoding]::UTF8.GetBytes(($Script -replace "`r`n", "`n"))
    $encoded = [Convert]::ToBase64String($bytes)
    return Invoke-Native "wsl.exe" @("-d", $Distro, "-u", "root", "--exec", "/bin/bash", "-c", "echo $encoded | base64 -d | bash")
}

# INI merge keeping every other line: returns $true when $Lines changed.
function Set-IniValue {
    param([System.Collections.Generic.List[string]]$Lines, [string]$Section, [string]$Key, [string]$Value)
    $header = -1
    for ($i = 0; $i -lt $Lines.Count; $i++) {
        if ($Lines[$i] -match ('^\s*\[\s*' + [regex]::Escape($Section) + '\s*\]\s*$')) { $header = $i; break }
    }
    if ($header -lt 0) {
        if ($Lines.Count -gt 0 -and $Lines[$Lines.Count - 1].Trim() -ne "") { $Lines.Add("") }
        $Lines.Add("[$Section]")
        $Lines.Add("$Key=$Value")
        return $true
    }
    $end = $Lines.Count
    for ($i = $header + 1; $i -lt $Lines.Count; $i++) {
        if ($Lines[$i] -match '^\s*\[') { $end = $i; break }
    }
    for ($i = $header + 1; $i -lt $end; $i++) {
        if ($Lines[$i] -match ('^\s*' + [regex]::Escape($Key) + '\s*=\s*(.*?)\s*$')) {
            if ($Matches[1] -eq $Value) { return $false }
            Note "$Section.$Key was '$($Matches[1])', now '$Value'"
            $Lines[$i] = "$Key=$Value"
            return $true
        }
    }
    $insert = $end
    while ($insert -gt $header + 1 -and $Lines[$insert - 1].Trim() -eq "") { $insert-- }
    $Lines.Insert($insert, "$Key=$Value")
    return $true
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    if ($WhatIfPreference) { Write-Warning "Not elevated: showing the plan only (run as administrator to apply)." }
    else { throw "Run this in an elevated PowerShell (Run as administrator)." }
}

Step "Windows version"
$os = Get-CimInstance Win32_OperatingSystem
$build = [int]$os.BuildNumber
Note "$($os.Caption), build $build"
if ($build -lt 22000) { throw "Windows 11 is required (WSL mirrored networking); this is build $build." }
if ($build -lt 22621) { throw "Windows 11 22H2 (build 22621) or later is required for networkingMode=mirrored; run Windows Update." }

Step "WSL"
$status = Invoke-Native "wsl.exe" @("--status")
$version = Invoke-Native "wsl.exe" @("--version")
if ($status.Code -ne 0 -or $version.Code -ne 0) {
    Note "WSL is not installed (or is the old inbox version)."
    if ($PSCmdlet.ShouldProcess("Windows", "wsl --install --no-distribution (enables WSL; restart needed)")) {
        $install = Invoke-Native "wsl.exe" @("--install", "--no-distribution")
        $install.Output | ForEach-Object { Note $_ }
        if ($install.Code -ne 0) { throw "wsl --install failed ($($install.Code))." }
        Write-Host ""
        Write-Host "Restart Windows, then run this script again." -ForegroundColor Yellow
        return
    }
} else {
    $line = ($version.Output | Where-Object { $_ -match '\d+\.\d+\.\d+' } | Select-Object -First 1)
    Note "WSL present: $line"
    if ($line -match '(\d+)\.(\d+)\.(\d+)' -and [int]$Matches[1] -lt 2) {
        Write-Warning "WSL $($Matches[0]) is older than 2.0 (mirrored networking). Run: wsl --update"
    }
}

Step "Distribution $Distro"
$distros = Get-Distros
if ($distros -contains $Distro) {
    Note "$Distro is installed."
} elseif ($PSCmdlet.ShouldProcess($Distro, "wsl --install -d $Distro (asks for a Linux user name and password)")) {
    Note "Installing $Distro. When it asks, create your Linux user; then type 'exit' to come back here."
    & wsl.exe --install -d $Distro
    $distros = Get-Distros
    if ($distros -notcontains $Distro) {
        throw "$Distro is not registered yet. Finish its first start (open '$Distro' from the Start menu), then run this again."
    }
}
$verbose = Invoke-Native "wsl.exe" @("--list", "--verbose")
$row = $verbose.Output | Where-Object { $_ -match ('\s' + [regex]::Escape($Distro) + '\s') } | Select-Object -First 1
if ($row -and $row -match '\s1\s*$') { Write-Warning "$Distro runs as WSL 1; convert it: wsl --set-version $Distro 2" }

Step ".wslconfig (mirrored networking)"
$wslconfig = Join-Path $env:USERPROFILE ".wslconfig"
Note $wslconfig
$lines = New-Object System.Collections.Generic.List[string]
if (Test-Path -LiteralPath $wslconfig) {
    foreach ($l in [IO.File]::ReadAllLines($wslconfig)) { $lines.Add($l) }
}
$section = ""
foreach ($l in $lines) {
    if ($l -match '^\s*\[\s*([^\]]+?)\s*\]') { $section = $Matches[1].ToLowerInvariant() }
    elseif ($section -eq "experimental" -and $l -match '^\s*networkingMode\s*=') {
        Write-Warning "networkingMode is also set under [experimental] (older WSL layout): remove that line by hand."
    }
}
$changed = $false
if (Set-IniValue $lines "wsl2" "networkingMode" "mirrored") { $changed = $true }
if (Set-IniValue $lines "experimental" "hostAddressLoopback" "true") { $changed = $true }
if (-not $changed) {
    Note "Already set."
} elseif ($PSCmdlet.ShouldProcess($wslconfig, "set networkingMode=mirrored, hostAddressLoopback=true (backup first)")) {
    if (Test-Path -LiteralPath $wslconfig) {
        $backup = "$wslconfig.bak-$(Get-Date -Format yyyyMMdd-HHmmss)"
        Copy-Item -LiteralPath $wslconfig -Destination $backup
        Note "Backup: $backup"
    }
    [IO.File]::WriteAllLines($wslconfig, $lines.ToArray(), (New-Object System.Text.UTF8Encoding($false)))
    Note "Written. It takes effect after: wsl --shutdown"
}

Step "systemd in $Distro"
if ($distros -notcontains $Distro) {
    Note "Skipped: $Distro is not installed yet."
} else {
    $check = Invoke-InDistro @'
[ -f /etc/wsl.conf ] || exit 1
awk '/^[[:space:]]*\[/ { boot = ($0 ~ /^[[:space:]]*\[boot\][[:space:]]*$/) }
     boot && /^[[:space:]]*systemd[[:space:]]*=[[:space:]]*true[[:space:]]*$/ { found = 1 }
     END { exit !found }' /etc/wsl.conf
'@
    if ($check.Code -eq 0) {
        Note "[boot] systemd=true is already set."
    } elseif ($PSCmdlet.ShouldProcess("$Distro /etc/wsl.conf", "set [boot] systemd=true (backup first)")) {
        $script = @'
set -eu
f=/etc/wsl.conf
if [ -f "$f" ]; then cp -p "$f" "$f.bak-$(date +%Y%m%d-%H%M%S)"; else : > "$f"; fi
awk '
  /^[[:space:]]*\[/ { if (boot && !done) { print "systemd=true"; done = 1 }
                      boot = ($0 ~ /^[[:space:]]*\[boot\][[:space:]]*$/); print; next }
  boot && /^[[:space:]]*systemd[[:space:]]*=/ { if (!done) { print "systemd=true"; done = 1 }; next }
  { print }
  END { if (!done) { if (!boot) print "[boot]"; print "systemd=true" } }' "$f" > "$f.new"
mv "$f.new" "$f"
echo "/etc/wsl.conf:"; sed 's/^/   /' "$f"
'@
        $result = Invoke-InDistro $script
        $result.Output | ForEach-Object { Note $_ }
        if ($result.Code -ne 0) { throw "Could not update /etc/wsl.conf ($($result.Code))." }
    }
    $pid1 = Invoke-Native "wsl.exe" @("-d", $Distro, "--exec", "ps", "-p", "1", "-o", "comm=")
    if (($pid1.Output -join "").Trim() -ne "systemd") {
        Note "systemd is not running yet: run 'wsl --shutdown', then open $Distro again."
    } else {
        Note "systemd is running."
    }
}

if ($ConfigureFirewall) {
    Step "Firewall (TCP $($Ports -join ', ') from $TailnetRange only)"
    $ruleName = "A2Core-Tailscale-TCP"
    $params = @{ Protocol = "TCP"; LocalPort = $Ports; RemoteAddress = $TailnetRange; Action = "Allow"; Profile = "Any" }
    $adapter = Get-NetAdapter -Name "Tailscale" -ErrorAction SilentlyContinue
    if ($adapter) { $params.InterfaceAlias = "Tailscale" } else { Write-Warning "No 'Tailscale' network adapter found; the rule is scoped by address only." }
    $existing = Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue
    if ($PSCmdlet.ShouldProcess("Windows Defender Firewall", "inbound rule $ruleName")) {
        if ($existing) { Set-NetFirewallRule -Name $ruleName @params; Note "Updated rule $ruleName." }
        else { New-NetFirewallRule -Name $ruleName -DisplayName "A2Core server (Tailscale only)" -Group "A2Core" -Direction Inbound @params | Out-Null; Note "Added rule $ruleName." }
    }
    # WSL in mirrored mode sits behind the Hyper-V firewall, which blocks inbound by default.
    $hyperv = Get-Command New-NetFirewallHyperVRule -ErrorAction SilentlyContinue
    if (-not $hyperv) {
        Write-Warning "New-NetFirewallHyperVRule is not available on this Windows; see README (Hyper-V firewall)."
    } elseif (-not $hyperv.Parameters.ContainsKey("RemoteAddresses")) {
        Write-Warning "New-NetFirewallHyperVRule has no RemoteAddresses parameter here; not adding an unscoped rule. See README."
    } else {
        $hvName = "A2Core-WSL-Tailscale-TCP"
        if (Get-NetFirewallHyperVRule -Name $hvName -ErrorAction SilentlyContinue) {
            Note "Hyper-V firewall rule $hvName exists."
        } elseif ($PSCmdlet.ShouldProcess("Hyper-V firewall (WSL)", "inbound rule $hvName")) {
            New-NetFirewallHyperVRule -Name $hvName -DisplayName "A2Core server in WSL (Tailscale only)" -Direction Inbound `
                -VMCreatorId $WslVmCreatorId -Protocol TCP -LocalPorts $Ports -RemoteAddresses $TailnetRange -Action Allow | Out-Null
            Note "Added Hyper-V firewall rule $hvName."
        }
    }
}

if ($RegisterStartupTask) {
    Step "Scheduled task '$TaskName'"
    $user = "$env:USERDOMAIN\$env:USERNAME"
    # A process that never exits keeps the distribution (and its systemd services) running.
    $wslArgs = "-d $Distro -u root --exec /bin/sleep infinity"
    if ($AtStartup) {
        $action = New-ScheduledTaskAction -Execute "wsl.exe" -Argument $wslArgs
        $trigger = New-ScheduledTaskTrigger -AtStartup
        $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType S4U -RunLevel Limited
    } else {
        $action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument "-NoProfile -WindowStyle Hidden -Command `"wsl.exe $wslArgs`""
        $trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
        $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
    }
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries `
        -DontStopIfGoingOnBatteries -StartWhenAvailable -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew
    $exists = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if ($PSCmdlet.ShouldProcess($TaskName, $(if ($exists) { "update scheduled task" } else { "register scheduled task" }))) {
        Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings `
            -Description "Keeps WSL $Distro running so the A2Core server's systemd services stay up (deploy/windows/README.md)." -Force | Out-Null
        Note "Registered. Start it now: Start-ScheduledTask -TaskName '$TaskName'"
    }
}

Step "Next steps"
Note "1. wsl --shutdown   (applies .wslconfig and systemd; stops all WSL distributions)"
Note "2. Tailscale for Windows: sign in; admin console > DNS: MagicDNS and HTTPS Certificates on."
Note "3. deploy\windows\renew-cert.ps1   (tailscale cert for this PC's ts.net name)"
Note "4. In ${Distro}: clone the repository to ~/a2core, put the data bundle in ~/a2core/data, then"
Note "   sudo deploy/windows/wsl-install.sh --data data/aion-server-data.tar.gz --certificate <cert> --key <key>"
Note "See deploy/windows/README.md."
