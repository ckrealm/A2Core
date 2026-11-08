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
NEW_SCRIPTS = [LINUX / 'backup.sh', LINUX / 'restore.sh', WINDOWS / 'wsl-install.sh']
SHELL_SCRIPTS = NEW_SCRIPTS + [LINUX / 'install.sh', LINUX / 'update.sh']
POSIX = os.name != 'nt' and shutil.which('bash') is not None

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
        for path in NEW_SCRIPTS + [WINDOWS / 'README.md', WINDOWS / 'setup-wsl.ps1', WINDOWS / 'renew-cert.ps1',
                                   LINUX / 'aion-backup.service', LINUX / 'aion-backup.timer']:
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

    @unittest.skipUnless(shutil.which('pwsh') or shutil.which('powershell'), 'needs PowerShell')
    def test_powershell_parses(self):
        shell = shutil.which('pwsh') or shutil.which('powershell')
        for path in (WINDOWS / 'setup-wsl.ps1', WINDOWS / 'renew-cert.ps1'):
            command = ("$e = $null; [System.Management.Automation.Language.Parser]::ParseFile("
                       f"'{path}', [ref]$null, [ref]$e) | Out-Null; if ($e) {{ $e | ForEach-Object {{ $_.ToString() }}; exit 1 }}")
            result = subprocess.run([shell, '-NoProfile', '-Command', command], capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, f'{path.name}: {result.stdout}{result.stderr}')

    def test_units_match_install(self):
        install = (LINUX / 'install.sh').read_text(encoding='utf-8')
        service = (LINUX / 'aion-backup.service').read_text(encoding='utf-8')
        self.assertIn('ExecStart=/opt/aion/bin/backup.sh', service)
        self.assertIn('/opt/aion/bin/', install)
        self.assertIn('aion-backup.timer', install)
        self.assertIn('Persistent=true', (LINUX / 'aion-backup.timer').read_text(encoding='utf-8'))
        # update.sh prunes only its own backups, not the nightly ones.
        self.assertIn('before-update-*.dump | tail -n +11', (LINUX / 'update.sh').read_text(encoding='utf-8'))


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
