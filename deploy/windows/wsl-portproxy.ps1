<#
.SYNOPSIS
Forwards the A2Core server's ports from this PC's Tailscale address to WSL (NAT networking; deploy/windows/README.md).

.DESCRIPTION
For WSL's default NAT networking (setup-wsl.ps1 -Networking Nat), where the distribution has its own address behind
Windows that changes with every WSL start. Run in an elevated PowerShell (Run as administrator). Safe to run again;
-WhatIf shows the changes without making them.

1. Finds the distribution's IPv4 address (wsl -d Ubuntu-24.04 hostname -I, the first IPv4 address; this starts the
   distribution when it is not running).
2. Finds the listen address: -ListenAddress, else this PC's Tailscale IPv4 address (100.64.0.0/10).
3. Warns about other Windows programs already listening on the ports.
4. For each server port (443, 3700, 8087, 8091, 8780, 13328):
     netsh interface portproxy add v4tov4 listenaddress=<listen> listenport=P connectaddress=<WSL IPv4> connectport=P
   An entry that is already right is kept; entries for these ports that point elsewhere (on the listen address,
   0.0.0.0, * or a 100.64.0.0/10 address) are replaced. Entries for other ports, and entries for these ports on other
   listen addresses (a warning names them), are never touched.
5. Unless -NoFirewall: the Windows Defender Firewall rule A2Core-Tailscale-TCP (the same as setup-wsl.ps1
   -ConfigureFirewall: those TCP ports, only from 100.64.0.0/10) when it is missing.
Optional:
   -Register   a scheduled task "A2Core WSL portproxy" that runs this again at startup, at logon and every 5 minutes
               (the WSL address changes on each boot and after wsl --shutdown). It runs as you, elevated, whether you
               are logged on or not (S4U); -LogonOnly registers it for your logon sessions only.
   -Remove     deletes exactly the portproxy entries step 4 manages and the scheduled task; nothing else.

.EXAMPLE
deploy\windows\wsl-portproxy.ps1 -WhatIf
deploy\windows\wsl-portproxy.ps1 -Register
deploy\windows\wsl-portproxy.ps1 -ListenAddress 0.0.0.0
deploy\windows\wsl-portproxy.ps1 -Remove
#>
#Requires -Version 5.1
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$Distro = "Ubuntu-24.04",
    [string]$ListenAddress = "",
    [switch]$Register,
    [switch]$LogonOnly,
    [switch]$Remove,
    [switch]$NoFirewall,
    [string]$LogFile = ""
)
$ErrorActionPreference = "Stop"
Set-StrictMode -Version 3
$Ports = @("443", "3700", "8087", "8091", "8780", "13328")
$TailnetRange = "100.64.0.0/10"
$TaskName = "A2Core WSL portproxy"
$RuleName = "A2Core-Tailscale-TCP"
$env:WSL_UTF8 = "1"   # wsl.exe prints UTF-16 otherwise

function Write-Log([string]$text) {
    if (-not $LogFile) { return }
    try {
        $dir = Split-Path -Parent $LogFile
        if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
        Add-Content -LiteralPath $LogFile -Value "$(Get-Date -Format s) $text"
    } catch { }
}
# The scheduled task runs hidden: a failure goes to the log too.
trap { Write-Log "ERROR: $_"; break }
function Step([string]$text) { Write-Host ""; Write-Host "== $text" -ForegroundColor Cyan }
function Note([string]$text) { Write-Host "   $text"; Write-Log $text }
function Warn([string]$text) { Write-Warning $text; Write-Log "WARNING: $text" }

function Invoke-Native {
    param([string]$File, [string[]]$Arguments)
    $ErrorActionPreference = "Continue"
    $output = & $File @Arguments 2>&1 | ForEach-Object { ("$_" -replace "`0", "") }
    return [pscustomobject]@{ Code = $LASTEXITCODE; Output = @($output) }
}

function Test-Ipv4([string]$text) {
    $address = $null
    return [Net.IPAddress]::TryParse($text, [ref]$address) -and $address.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork -and
        $text -match '^\d{1,3}(\.\d{1,3}){3}$'
}
function Test-Tailnet([string]$text) {
    return $text -match '^100\.(\d{1,3})\.\d{1,3}\.\d{1,3}$' -and [int]$Matches[1] -ge 64 -and [int]$Matches[1] -le 127
}

# This PC's Tailscale IPv4: the 'Tailscale' adapter's address, else any 100.64.0.0/10 address, else `tailscale ip -4`.
function Get-TailscaleIpv4 {
    $all = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { Test-Tailnet $_.IPAddress })
    $named = @($all | Where-Object { $_.InterfaceAlias -eq "Tailscale" })
    if ($named.Count -ge 1) { return $named[0].IPAddress }
    if ($all.Count -eq 1) { return $all[0].IPAddress }
    if (Get-Command tailscale.exe -ErrorAction SilentlyContinue) {
        $ip = Invoke-Native "tailscale.exe" @("ip", "-4")
        $first = @($ip.Output | ForEach-Object { $_.Trim() } | Where-Object { Test-Tailnet $_ }) | Select-Object -First 1
        if ($ip.Code -eq 0 -and $first) { return $first }
    }
    return $null
}

# Current v4tov4 entries: netsh's table rows (the header text is localized, the rows are not).
function Get-PortProxies {
    $result = Invoke-Native "netsh.exe" @("interface", "portproxy", "show", "v4tov4")
    $rows = @()
    foreach ($line in $result.Output) {
        if ($line -match '^\s*(\S+)\s+(\d+)\s+(\S+)\s+(\d+)\s*$') {
            $rows += [pscustomobject]@{ ListenAddress = $Matches[1]; ListenPort = $Matches[2]; ConnectAddress = $Matches[3]; ConnectPort = $Matches[4] }
        }
    }
    return $rows
}

# Entries this script manages: one of our ports, on the listen address, a wildcard or a Tailscale address
# (a run before the Tailscale address changed).
function Test-Managed($row, [string]$listen) {
    if ($Ports -notcontains $row.ListenPort) { return $false }
    return $row.ListenAddress -eq $listen -or $row.ListenAddress -eq "0.0.0.0" -or $row.ListenAddress -eq "*" -or (Test-Tailnet $row.ListenAddress)
}

function Remove-Proxy($row) {
    if ($PSCmdlet.ShouldProcess("portproxy $($row.ListenAddress):$($row.ListenPort) -> $($row.ConnectAddress):$($row.ConnectPort)", "delete")) {
        $r = Invoke-Native "netsh.exe" @("interface", "portproxy", "delete", "v4tov4", "listenport=$($row.ListenPort)", "listenaddress=$($row.ListenAddress)")
        if ($r.Code -ne 0) { Warn "netsh delete $($row.ListenAddress):$($row.ListenPort) failed: $($r.Output -join ' ')" }
        else { Note "Deleted $($row.ListenAddress):$($row.ListenPort) -> $($row.ConnectAddress):$($row.ConnectPort)" }
    }
}

function Test-Listening([string]$address, [string]$port) {
    $listeners = @(Get-NetTCPConnection -State Listen -LocalPort ([int]$port) -ErrorAction SilentlyContinue |
        Where-Object { $_.LocalAddress -eq $address })
    return $listeners.Count -gt 0
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    if ($WhatIfPreference) { Write-Warning "Not elevated: showing the plan only (run as administrator to apply)." }
    else { throw "Run this in an elevated PowerShell (Run as administrator)." }
}

if ($Remove) {
    Step "Removing the portproxy entries for TCP $($Ports -join ', ')"
    $rows = @(Get-PortProxies | Where-Object { Test-Managed $_ $ListenAddress })
    if ($rows.Count -eq 0) { Note "None." }
    foreach ($row in $rows) { Remove-Proxy $row }
    if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
        if ($PSCmdlet.ShouldProcess($TaskName, "unregister scheduled task")) {
            Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
            Note "Unregistered task '$TaskName'."
        }
    }
    Note "The firewall rule $RuleName stays (setup-wsl.ps1 -ConfigureFirewall owns it): Remove-NetFirewallRule -Name $RuleName"
    return
}

Step "WSL address of $Distro"
$hostname = Invoke-Native "wsl.exe" @("-d", $Distro, "--exec", "hostname", "-I")
$wslIp = @(($hostname.Output -join " ") -split '\s+' | Where-Object { $_ -and (Test-Ipv4 $_) }) | Select-Object -First 1
if ($hostname.Code -ne 0 -or -not $wslIp) {
    throw "Could not read $Distro's IPv4 address (wsl -d $Distro hostname -I: $($hostname.Output -join ' ')). Is it installed and running?"
}
if (Test-Tailnet $wslIp) { Warn "$Distro has a Tailscale address ($wslIp): it looks like mirrored networking, where no portproxy is needed." }
Note "$Distro is at $wslIp"

Step "Listen address"
if (-not $ListenAddress) {
    $ListenAddress = Get-TailscaleIpv4
    if (-not $ListenAddress) { throw "No Tailscale IPv4 address on this PC (is Tailscale running and signed in?). Or pass -ListenAddress." }
    Note "$ListenAddress (this PC's Tailscale address)"
} elseif (-not (Test-Ipv4 $ListenAddress)) {
    throw "-ListenAddress must be an IPv4 address (the Tailscale address, or 0.0.0.0 for every address)."
} else {
    Note "$ListenAddress (from -ListenAddress)"
    if ($ListenAddress -eq "0.0.0.0") { Note "Every Windows address listens; only the firewall rule keeps the ports to Tailscale peers." }
}

$service = Get-Service iphlpsvc -ErrorAction SilentlyContinue
if (-not $service) { throw "The IP Helper service (iphlpsvc), which carries netsh portproxy, is missing." }
if ($service.Status -ne "Running") {
    if ($PSCmdlet.ShouldProcess("IP Helper (iphlpsvc)", "start service (it carries the portproxy listeners)")) {
        if ($service.StartType -eq "Disabled") { Set-Service iphlpsvc -StartupType Automatic }
        Start-Service iphlpsvc
        Note "Started the IP Helper service."
    }
}

Step "Ports on Windows"
$iphlp = Get-CimInstance Win32_Service -Filter "Name='iphlpsvc'" -ErrorAction SilentlyContinue
$iphlpPid = if ($iphlp) { [int]$iphlp.ProcessId } else { -1 }
$clash = $false
foreach ($port in $Ports) {
    foreach ($c in @(Get-NetTCPConnection -State Listen -LocalPort ([int]$port) -ErrorAction SilentlyContinue)) {
        if ([int]$c.OwningProcess -eq $iphlpPid) { continue }   # portproxy itself
        $process = Get-Process -Id $c.OwningProcess -ErrorAction SilentlyContinue
        $name = if ($process) { $process.ProcessName } else { "pid $($c.OwningProcess)" }
        if ($name -eq "wslrelay") { continue }                  # WSL's localhost forwarding of the server itself
        $overlaps = $c.LocalAddress -in @($ListenAddress, "0.0.0.0", "::") -or $ListenAddress -eq "0.0.0.0"
        if ($overlaps) {
            Warn "TCP $port is already listened on by $name (pid $($c.OwningProcess)) on $($c.LocalAddress): portproxy cannot take it; stop that program."
            $clash = $true
        }
    }
}
if (-not $clash) { Note "TCP $($Ports -join ', ') free on $ListenAddress." }

Step "Portproxy $ListenAddress -> $wslIp"
$rows = @(Get-PortProxies)
foreach ($row in $rows) {
    if ($Ports -contains $row.ListenPort -and -not (Test-Managed $row $ListenAddress)) {
        Warn "Leaving portproxy $($row.ListenAddress):$($row.ListenPort) -> $($row.ConnectAddress):$($row.ConnectPort) (not one of ours)."
    }
}
$changes = 0
foreach ($port in $Ports) {
    $mine = @($rows | Where-Object { $_.ListenPort -eq $port -and (Test-Managed $_ $ListenAddress) })
    $right = @($mine | Where-Object { $_.ListenAddress -eq $ListenAddress -and $_.ConnectAddress -eq $wslIp -and $_.ConnectPort -eq $port })
    foreach ($row in $mine) { if ($right -notcontains $row) { Remove-Proxy $row; $changes++ } }
    # A right entry whose listener is gone (the address appeared after iphlpsvc started) is added again.
    if ($right.Count -gt 0 -and ($WhatIfPreference -or (Test-Listening $ListenAddress $port))) { continue }
    if ($right.Count -gt 0) { Note "TCP $port has an entry but no listener: adding it again." }
    if ($PSCmdlet.ShouldProcess("portproxy ${ListenAddress}:$port -> ${wslIp}:$port", "add")) {
        $r = Invoke-Native "netsh.exe" @("interface", "portproxy", "add", "v4tov4", "listenaddress=$ListenAddress", "listenport=$port",
            "connectaddress=$wslIp", "connectport=$port")
        if ($r.Code -ne 0) { Warn "netsh add ${ListenAddress}:$port failed: $($r.Output -join ' ')" }
        else { Note "${ListenAddress}:$port -> ${wslIp}:$port"; $changes++ }
    }
}
if ($changes -eq 0) { Note "Up to date." }

if (-not $NoFirewall) {
    Step "Firewall rule $RuleName (TCP $($Ports -join ', ') from $TailnetRange only)"
    if (Get-NetFirewallRule -Name $RuleName -ErrorAction SilentlyContinue) {
        Note "Present (setup-wsl.ps1 -ConfigureFirewall updates it)."
    } elseif ($PSCmdlet.ShouldProcess("Windows Defender Firewall", "inbound rule $RuleName")) {
        $params = @{ Protocol = "TCP"; LocalPort = $Ports; RemoteAddress = $TailnetRange; Action = "Allow"; Profile = "Any" }
        if (Get-NetAdapter -Name "Tailscale" -ErrorAction SilentlyContinue) { $params.InterfaceAlias = "Tailscale" }
        else { Warn "No 'Tailscale' network adapter found; the rule is scoped by address only." }
        New-NetFirewallRule -Name $RuleName -DisplayName "A2Core server (Tailscale only)" -Group "A2Core" -Direction Inbound @params | Out-Null
        Note "Added rule $RuleName."
    }
}

if ($Register) {
    Step "Scheduled task '$TaskName'"
    if (-not $PSCommandPath) { throw "-Register needs the script run from its file." }
    $user = "$env:USERDOMAIN\$env:USERNAME"
    $log = Join-Path $env:ProgramData "A2Core\wsl-portproxy.log"
    $arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$PSCommandPath`" -Distro `"$Distro`" -NoFirewall -LogFile `"$log`""
    if ($PSBoundParameters.ContainsKey("ListenAddress") -and $ListenAddress) { $arguments += " -ListenAddress $ListenAddress" }
    $action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument $arguments
    $repeat = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 5)
    $logon = New-ScheduledTaskTrigger -AtLogOn -User $user
    $logon.Delay = "PT30S"
    if ($LogonOnly) {
        $triggers = @($logon, $repeat)
        $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
    } else {
        $startup = New-ScheduledTaskTrigger -AtStartup
        $startup.Delay = "PT1M"   # Tailscale and WSL come up first
        $triggers = @($startup, $logon, $repeat)
        $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType S4U -RunLevel Highest
    }
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 4) -AllowStartIfOnBatteries `
        -DontStopIfGoingOnBatteries -StartWhenAvailable -MultipleInstances IgnoreNew
    $exists = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if ($PSCmdlet.ShouldProcess($TaskName, $(if ($exists) { "update scheduled task" } else { "register scheduled task" }))) {
        Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $triggers -Principal $principal -Settings $settings `
            -Description "Points the A2Core portproxy entries at WSL $Distro's current address (deploy/windows/wsl-portproxy.ps1). Log: $log" -Force | Out-Null
        Note "Registered: runs $PSCommandPath (keep the script there). Log: $log"
    }
}
