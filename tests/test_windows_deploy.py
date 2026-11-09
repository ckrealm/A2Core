"""Windows hosting (deploy/windows) and the Linux backup scripts: syntax, backup retention, restore refusal, game.env."""
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import textwrap
import unittest

ROOT = Path(__file__).resolve().parents[1]
LINUX = ROOT / 'deploy/linux'
WINDOWS = ROOT / 'deploy/windows'
NEW_SCRIPTS = [LINUX / 'backup.sh', LINUX / 'restore.sh', LINUX / 'backup-paths.sh', WINDOWS / 'wsl-install.sh']
SHELL_SCRIPTS = NEW_SCRIPTS + [LINUX / 'install.sh', LINUX / 'update.sh']
POWERSHELL_SCRIPTS = [WINDOWS / 'setup-wsl.ps1', WINDOWS / 'renew-cert.ps1', WINDOWS / 'wsl-portproxy.ps1']
POSIX = os.name != 'nt' and shutil.which('bash') is not None
PWSH = shutil.which('pwsh') or shutil.which('powershell')


def run_pwsh(script, *args):
    """Runs PowerShell code from a temporary .ps1 file (so no quoting through -Command)."""
    with tempfile.TemporaryDirectory() as temp:
        path = Path(temp) / 'test.ps1'
        path.write_text(script, encoding='utf-8')
        return subprocess.run([PWSH, '-NoProfile', '-NonInteractive', '-File', str(path), *map(str, args)],
                              capture_output=True, text=True)


# Loads the named functions of a script (by its syntax tree, so nothing else in it runs).
LOAD_FUNCTIONS = '''
param([string]$Script, [string]$Names)
$ast = [System.Management.Automation.Language.Parser]::ParseFile($Script, [ref]$null, [ref]$null)
$wanted = $Names -split ','
foreach ($f in $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $wanted -contains $n.Name }, $false)) {
    . ([scriptblock]::Create($f.Extent.Text.Replace('function ' + $f.Name, 'function global:' + $f.Name)))
}
'''

# Stands in for tools/postgres_dev.py: `backup` writes a file, `verify-backup` succeeds unless STUB_VERIFY_FAIL is set.
STUB = textwrap.dedent('''\
    import os, sys
    from pathlib import Path
    command = sys.argv[1]
    if command == 'backup':
        out = Path(sys.argv[2])
        if out.exists():
            sys.exit('Backup destination already exists')
        out.parent.mkdir(parents=True, exist_ok=True)
        out.write_bytes(b'PGDMP stub')
        print('Backup saved:', out)
    elif command == 'verify-backup':
        if os.environ.get('STUB_VERIFY_FAIL'):
            sys.exit('pg_restore failed')
        print('items=1')
        print('migrations=1')
    else:
        sys.exit('unexpected command ' + command)
''')


class FilesTests(unittest.TestCase):
    def test_files_exist(self):
        for path in NEW_SCRIPTS + POWERSHELL_SCRIPTS + [WINDOWS / 'README.md', LINUX / 'aion-backup.service',
                                                         LINUX / 'aion-backup.timer']:
            self.assertTrue(path.is_file(), path)

    @unittest.skipUnless(POSIX, 'needs bash')
    def test_shell_syntax(self):
        for path in SHELL_SCRIPTS:
            result = subprocess.run(['bash', '-n', str(path)], capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, f'{path.name}: {result.stderr}')
            self.assertTrue(os.access(path, os.X_OK), f'{path.name} is not executable')

    @unittest.skipUnless(POSIX and shutil.which('shellcheck'), 'needs shellcheck')
    def test_shellcheck(self):
        result = subprocess.run(['shellcheck', '--severity=info', *map(str, NEW_SCRIPTS)], capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stdout)

    @unittest.skipUnless(PWSH, 'needs PowerShell')
    def test_powershell_parses(self):
        for path in POWERSHELL_SCRIPTS:
            command = ("$e = $null; [System.Management.Automation.Language.Parser]::ParseFile("
                       f"'{path}', [ref]$null, [ref]$e) | Out-Null; if ($e) {{ $e | ForEach-Object {{ $_.ToString() }}; exit 1 }}")
            result = subprocess.run([PWSH, '-NoProfile', '-Command', command], capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, f'{path.name}: {result.stdout}{result.stderr}')

    def test_portproxy_script_covers_the_server_ports(self):
        portproxy = (WINDOWS / 'wsl-portproxy.ps1').read_text(encoding='utf-8')
        setup = (WINDOWS / 'setup-wsl.ps1').read_text(encoding='utf-8')
        ports = '$Ports = @("443", "3700", "8087", "8091", "8780", "13328")'
        self.assertIn(ports, portproxy)
        self.assertIn(ports, setup)
        # The same firewall rule name and range as setup-wsl.ps1 -ConfigureFirewall.
        for text in ('"A2Core-Tailscale-TCP"', '$TailnetRange = "100.64.0.0/10"'):
            self.assertIn(text, portproxy)
            self.assertIn(text, setup)
        self.assertIn('SupportsShouldProcess = $true', portproxy)
        self.assertIn('[ValidateSet("Auto", "Mirrored", "Nat")]', setup)


    def test_units_match_install(self):
        install = (LINUX / 'install.sh').read_text(encoding='utf-8')
        service = (LINUX / 'aion-backup.service').read_text(encoding='utf-8')
        self.assertIn('ExecStart=/opt/aion/bin/backup.sh', service)
        self.assertIn('/opt/aion/bin/', install)
        self.assertIn('aion-backup.timer', install)
        self.assertIn('Persistent=true', (LINUX / 'aion-backup.timer').read_text(encoding='utf-8'))
        # Custom backup folders: the sandboxed unit gets them through backup-paths.sh's drop-in, which install.sh runs.
        self.assertIn('ProtectSystem=strict', service)
        self.assertIn('backup-paths.sh', service)
        self.assertIn('"$REPO/deploy/linux/backup-paths.sh" /opt/aion/bin/', install)
        self.assertIn('/opt/aion/bin/backup-paths.sh', install)
        for readme in (LINUX / 'README.md', WINDOWS / 'README.md'):
            text = readme.read_text(encoding='utf-8')
            for word in ('AION_BACKUP_COPY_DIR', 'AION_BACKUP_COPY_KEEP', 'backup-paths.sh'):
                self.assertIn(word, text, f'{readme}: {word}')
        # update.sh prunes only its own backups, not the nightly ones.
        self.assertIn('before-update-*.dump | tail -n +11', (LINUX / 'update.sh').read_text(encoding='utf-8'))


@unittest.skipUnless(PWSH, 'needs PowerShell')
class PowerShellFunctionTests(unittest.TestCase):
    def test_wslconfig_nat_removes_only_the_mirrored_keys(self):
        script = LOAD_FUNCTIONS + '''
function Lines([string[]]$items) { $l = New-Object System.Collections.Generic.List[string]; foreach ($i in $items) { $l.Add($i) }; return ,$l }
$original = @('[wsl2]', 'memory=12GB', 'swap=4GB')
$l = Lines $original
Set-IniValue $l "wsl2" "networkingMode" "mirrored" | Out-Null
Set-IniValue $l "experimental" "hostAddressLoopback" "true" | Out-Null
if (-not ($l -contains 'networkingMode=mirrored')) { throw "mirrored not set: $($l -join '|')" }
if (-not (Remove-IniValue $l "wsl2" "networkingMode")) { throw "networkingMode not removed" }
if (-not (Remove-IniValue $l "experimental" "hostAddressLoopback")) { throw "hostAddressLoopback not removed" }
if (($l -join '|') -ne ($original -join '|')) { throw "after NAT: $($l -join '|')" }
if (Remove-IniValue $l "wsl2" "networkingMode") { throw "second removal changed something" }
$m = Lines @('[experimental]', 'hostAddressLoopback=true', 'sparseVhd=true', '', '[wsl2]', 'networkingMode=mirrored', 'memory=6GB')
Remove-IniValue $m "wsl2" "networkingMode" | Out-Null
Remove-IniValue $m "experimental" "hostAddressLoopback" | Out-Null
if (($m -join '|') -ne '[experimental]|sparseVhd=true||[wsl2]|memory=6GB') { throw "other keys: $($m -join '|')" }
'ok'
'''
        result = run_pwsh(script, WINDOWS / 'setup-wsl.ps1', 'Note,Set-IniValue,Remove-IniValue')
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn('ok', result.stdout)

    def test_portproxy_rows_and_managed_entries(self):
        script = LOAD_FUNCTIONS + '''
$global:Ports = @("443", "3700", "8087", "8091", "8780", "13328")
function global:Invoke-Native { [pscustomobject]@{ Code = 0; Output = @(
    '', 'Listen on ipv4:             Connect to ipv4:', '', 'Address         Port        Address         Port',
    '--------------- ----------  --------------- ----------',
    '0.0.0.0         443         172.20.1.2      443', '100.101.1.2     8780        172.20.1.2      8780',
    '192.168.1.5     3700        10.0.0.1        3700', '0.0.0.0         9000        172.20.1.2      9000') } }
$rows = @(Get-PortProxies)
if ($rows.Count -ne 4) { throw "rows: $($rows.Count)" }
$managed = @($rows | Where-Object { Test-Managed $_ "100.101.1.2" } | ForEach-Object { "$($_.ListenAddress):$($_.ListenPort)" })
if (($managed -join ',') -ne '0.0.0.0:443,100.101.1.2:8780') { throw "managed: $($managed -join ',')" }
if (-not (Test-Ipv4 '172.20.1.2') -or (Test-Ipv4 'fe80::1') -or (Test-Ipv4 '1.2.3')) { throw 'Test-Ipv4' }
if (-not (Test-Tailnet '100.64.0.1') -or (Test-Tailnet '100.128.0.1') -or (Test-Tailnet '10.0.0.1')) { throw 'Test-Tailnet' }
'ok'
'''
        result = run_pwsh(script, WINDOWS / 'wsl-portproxy.ps1', 'Get-PortProxies,Test-Managed,Test-Ipv4,Test-Tailnet')
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn('ok', result.stdout)


@unittest.skipUnless(POSIX and shutil.which('flock'), 'needs bash and flock (Linux)')
class BackupScriptTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.tool = self.root / 'postgres_dev.py'
        self.tool.write_text(STUB, encoding='utf-8')
        self.backups = self.root / 'backups'
        self.backups.mkdir()

    def tearDown(self):
        self.temp.cleanup()

    def run_script(self, script, *args, **env):
        environment = dict(os.environ, AION_PG_TOOL=str(self.tool), AION_PYTHON=sys.executable, AION_RUN_AS='',
                           AION_BACKUP_DIR=str(self.backups), AION_PG_HOME=str(self.root / 'pg'), **env)
        return subprocess.run(['bash', str(script), *args], capture_output=True, text=True, env=environment)

    def names(self):
        return sorted(path.name for path in self.backups.iterdir())

    def test_retention_keeps_newest_of_its_prefix(self):
        old = [f'nightly-2020010{day}-033000.dump' for day in range(1, 6)]
        others = ['before-update-20200101-000000.dump', 'aion-20200101-000000.dump', 'nightly-notes.dump']
        for name in old + others:
            (self.backups / name).write_bytes(b'x')
        result = self.run_script(LINUX / 'backup.sh', '--keep', '3')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn('items=1', result.stdout)
        nightly = [name for name in self.names() if name.startswith('nightly-2') and name not in old]
        self.assertEqual(len(nightly), 1)
        self.assertRegex(nightly[0], r'^nightly-\d{8}-\d{6}\.dump$')
        # The new backup and the two newest old ones stay; other kinds and odd names are never pruned.
        self.assertEqual(self.names(), sorted(nightly + old[3:] + others))
        self.assertFalse(list(self.backups.glob('*.partial')))

    def test_failed_verification_discards_and_prunes_nothing(self):
        old = [f'nightly-2020010{day}-033000.dump' for day in range(1, 4)]
        for name in old:
            (self.backups / name).write_bytes(b'x')
        result = self.run_script(LINUX / 'backup.sh', '--keep', '1', STUB_VERIFY_FAIL='1')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('Verification failed', result.stderr)
        self.assertEqual(self.names(), old)

    def test_no_verify_and_prefix(self):
        result = self.run_script(LINUX / 'backup.sh', '--no-verify', '--prefix', 'weekly', STUB_VERIFY_FAIL='1')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertNotIn('items=1', result.stdout)
        self.assertRegex(' '.join(self.names()), r'^weekly-\d{8}-\d{6}\.dump$')

    def test_copy_dir_gets_each_verified_backup_with_its_own_retention(self):
        copies = self.root / 'offsite'
        copies.mkdir()
        old = [f'nightly-2020010{day}-033000.dump' for day in range(1, 5)]
        for name in old + ['keep-me.txt']:
            (copies / name).write_bytes(b'x')
        result = self.run_script(LINUX / 'backup.sh', '--keep', '5', AION_BACKUP_COPY_DIR=str(copies), AION_BACKUP_COPY_KEEP='2')
        self.assertEqual(result.returncode, 0, result.stderr)
        new = [name for name in self.names() if name.startswith('nightly-')]
        self.assertEqual(len(new), 1)
        self.assertIn('Copied to', result.stdout)
        self.assertEqual((copies / new[0]).read_bytes(), (self.backups / new[0]).read_bytes())
        self.assertEqual(sorted(p.name for p in copies.iterdir()), sorted([new[0], old[-1], 'keep-me.txt']))

    def test_failed_copy_keeps_the_backup_and_fails(self):
        blocker = self.root / 'not-a-folder'
        blocker.write_bytes(b'x')
        result = self.run_script(LINUX / 'backup.sh', '--copy-dir', str(blocker / 'sub'))
        self.assertEqual(result.returncode, 1)
        self.assertIn('Copy to', result.stderr)
        self.assertEqual(len([name for name in self.names() if name.startswith('nightly-')]), 1)
        self.assertEqual(self.run_script(LINUX / 'backup.sh', '--copy-dir', 'relative').returncode, 2)
        self.assertEqual(self.run_script(LINUX / 'backup.sh', '--copy-keep', '0').returncode, 2)

    def run_paths(self, env_text):
        env_file = self.root / 'backup.env'
        env_file.write_text(env_text, encoding='utf-8')
        units = self.root / 'systemd'
        result = subprocess.run(['bash', str(LINUX / 'backup-paths.sh')], capture_output=True, text=True,
                                env=dict(os.environ, AION_BACKUP_ENV=str(env_file), AION_SYSTEMD_DIR=str(units),
                                         AION_RUN_AS='', AION_NO_RELOAD='1'))
        return result, units / 'aion-backup.service.d' / '10-backup-paths.conf'

    def test_backup_paths_writes_the_units_read_write_paths(self):
        custom = self.root / 'mnt d' / 'aion-backups'
        copies = self.root / 'copies'
        result, drop_in = self.run_paths(f'AION_BACKUP_KEEP=7\nAION_BACKUP_DIR="{custom}"\nAION_BACKUP_COPY_DIR={copies}\n')
        self.assertEqual(result.returncode, 0, result.stderr)
        text = drop_in.read_text(encoding='utf-8')
        self.assertIn('[Service]', text)
        self.assertIn(f'ReadWritePaths="-{custom}"', text)
        self.assertIn(f'ReadWritePaths="-{copies}"', text)
        self.assertTrue(custom.is_dir() and copies.is_dir())
        # Back to the default folder: the drop-in goes.
        result, drop_in = self.run_paths('AION_BACKUP_DIR=/var/lib/aion/backups\n')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertFalse(drop_in.exists())

    def test_backup_paths_refuses_what_the_unit_cannot_use(self):
        for value in ('relative/dir', '/home/me/backups', '/srv/a%n', '/srv/"x"', '/srv/../etc'):
            result, drop_in = self.run_paths(f'AION_BACKUP_COPY_DIR={value}\n')
            self.assertEqual(result.returncode, 2, value)
            self.assertFalse(drop_in.exists(), value)

    def test_bad_options(self):
        for args in (['--keep', '0'], ['--keep', 'x'], ['--prefix', '../x'], ['--bogus']):
            self.assertEqual(self.run_script(LINUX / 'backup.sh', *args).returncode, 2, args)

    def test_restore_checks_then_refuses_without_yes(self):
        dump = self.backups / 'nightly-20200101-000000.dump'
        dump.write_bytes(b'x')
        refused = self.run_script(LINUX / 'restore.sh', str(dump))
        self.assertEqual(refused.returncode, 2)
        self.assertIn('items=1', refused.stdout)
        self.assertIn('Refusing', refused.stderr)
        checked = self.run_script(LINUX / 'restore.sh', '--check', str(dump))
        self.assertEqual(checked.returncode, 0, checked.stderr)
        self.assertIn('Restore test passed', checked.stdout)
        bad = self.run_script(LINUX / 'restore.sh', '--check', str(dump), STUB_VERIFY_FAIL='1')
        self.assertNotEqual(bad.returncode, 0)
        self.assertEqual(self.names(), [dump.name])

    def test_restore_drop_old_needs_a_restore_name_and_yes(self):
        self.assertEqual(self.run_script(LINUX / 'restore.sh', '--yes', '--drop-old', 'aion').returncode, 2)
        self.assertEqual(self.run_script(LINUX / 'restore.sh', '--drop-old', 'aion_pre_restore_20200101_000000').returncode, 2)
        self.assertEqual(self.run_script(LINUX / 'restore.sh', '--check', '--yes', 'x.dump').returncode, 2)


@unittest.skipUnless(POSIX, 'needs bash')
class WslInstallTests(unittest.TestCase):
    def run_install(self, config, *args):
        return subprocess.run(['bash', str(WINDOWS / 'wsl-install.sh'), *args], capture_output=True, text=True,
                              env=dict(os.environ, AION_CONFIG_DIR=str(config)))

    def test_rates_only_merges_game_env(self):
        with tempfile.TemporaryDirectory() as temp:
            config = Path(temp)
            (config / 'game.env').write_text('# my note\nAion__Rates__Experience=4\nAion__Rules__RespawnSeconds=20\n',
                                              encoding='utf-8')
            result = self.run_install(config, '--rates-only', '--rates-profile', 'accelerated', '--exp-rate', '2.5',
                                      '--max-players', '10', '--pvp', 'off')
            self.assertEqual(result.returncode, 0, result.stderr)
            lines = (config / 'game.env').read_text(encoding='utf-8').splitlines()
            self.assertEqual(lines[:3], ['# my note', 'Aion__Rates__Experience=2.5', 'Aion__Rules__RespawnSeconds=20'])
            self.assertEqual(sorted(lines[3:]), ['Aion__Native__MaxPlayers=10', 'Aion__Rates__Profile=accelerated',
                                                 'Aion__Rules__Pvp=false'])
            self.assertEqual((config / 'game.env').stat().st_mode & 0o777, 0o640)

    def test_reset_options(self):
        with tempfile.TemporaryDirectory() as temp:
            result = self.run_install(Path(temp), '--rates-only', '--reset-hour', '05', '--reset-day', 'wednesday',
                                      '--reset-timezone', 'America/Chicago')
            self.assertEqual(result.returncode, 0, result.stderr)
            lines = sorted((Path(temp) / 'game.env').read_text(encoding='utf-8').splitlines())
            self.assertEqual(lines, ['Aion__Resets__Hour=5', 'Aion__Resets__TimeZone=America/Chicago', 'Aion__Resets__WeeklyDay=Wednesday'])
            for args in (['--rates-only', '--reset-hour', '24'], ['--rates-only', '--reset-day', 'someday'],
                         ['--rates-only', '--reset-timezone', 'a;b']):
                self.assertEqual(self.run_install(Path(temp), *args).returncode, 2, args)

    def test_public_address_option(self):
        with tempfile.TemporaryDirectory() as temp:
            result = self.run_install(Path(temp), '--public-address', '100.64.0.1', '--rates-only', '--pvp', 'on')
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn('Aion__Rules__Pvp=true', (Path(temp) / 'game.env').read_text(encoding='utf-8'))
        help_text = self.run_install(Path(temp), '--help').stdout
        self.assertIn('--public-address', help_text)
        self.assertIn('NAT networking', help_text)

    def test_rejects_bad_values(self):
        with tempfile.TemporaryDirectory() as temp:
            for args in (['--rates-only', '--rates-profile', 'fast'], ['--rates-only', '--exp-rate', '101'],
                         ['--rates-only', '--respawn-seconds', '2'], ['--rates-only', '--pvp', 'maybe'],
                         ['--rates-only', '--max-players', '-1'], ['--rates-only'],
                         ['--data', 'x.tar.gz'], ['--certificate', 'cert.pem'],
                         ['--self-signed', '--certificate', 'c', '--key', 'k']):
                result = self.run_install(Path(temp), *args)
                self.assertEqual(result.returncode, 2, f'{args}: {result.stderr}')
            self.assertFalse((Path(temp) / 'game.env').exists())


if __name__ == '__main__':
    unittest.main()
