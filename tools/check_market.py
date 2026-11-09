"""Run tests/Aion.Market.Checks against a fresh disposable PostgreSQL database (docs/MARKET.md).

The checks build their own synthetic catalogue rows (Kinah, a stackable material, a sword); admin credentials only
create, migrate, seed and drop the isolated database, as tools/check_persistence.py does.
"""
import json
import os
import subprocess
import uuid

try:
    from .postgres_dev import ROOT, HOME, PORT, sql, migration_script
except ImportError:
    from postgres_dev import ROOT, HOME, PORT, sql, migration_script


def main():
    assembly = ROOT / 'tests/Aion.Market.Checks/bin/Debug/net10.0/Aion.Market.Checks.dll'
    if os.environ.get('AION_ARTIFACTS_PATH'):
        assembly = ROOT / os.environ['AION_ARTIFACTS_PATH'] / 'bin/Aion.Market.Checks/debug/Aion.Market.Checks.dll'
    if not assembly.exists():
        raise ValueError('Build tests/Aion.Market.Checks/Aion.Market.Checks.csproj first')
    name = 'aion_repository_test_' + uuid.uuid4().hex
    accounts = [str(uuid.uuid4()) for _ in range(4)]
    characters = [str(uuid.uuid4()) for _ in range(4)]
    credentials = json.loads((HOME / 'credentials.json').read_text())
    sql(f'CREATE DATABASE {name} OWNER aion_owner;', database='postgres')
    try:
        sql(f'REVOKE ALL ON DATABASE {name} FROM PUBLIC; GRANT CONNECT ON DATABASE {name} TO aion_app;', database=name)
        sql(migration_script(sorted((ROOT / 'database/migrations').glob('*.sql'))), database=name)
        sql("""
            INSERT INTO catalog.builds VALUES('market-test','{}',now());
            INSERT INTO catalog.items VALUES
              ('market-test',930100001,'Kinah','currency','{"facts":{"ItemType":"EItemType::Currency"}}'),
              ('market-test',501,'Test Iron Ore','misc','{"facts":{"ItemType":"EItemType::Misc"}}'),
              ('market-test',502,'Test Ore Dust','misc','{"facts":{"ItemType":"EItemType::Misc"}}'),
              ('market-test',601,'Test Sword','sword','{"facts":{"ItemType":"EItemType::Equip"}}');
            INSERT INTO game.item_stacks VALUES(501,1000),(502,1000) ON CONFLICT DO NOTHING;
            """, database=name)
        # Seller, buyer and a poor buyer in world 1204; another character in world 1205.
        for account, character, character_name, world in zip(accounts, characters, ['Seller', 'Buyer', 'Poor', 'Faraway'], [1204, 1204, 1204, 1205]):
            sql(f"INSERT INTO auth.accounts(account_id,provider,subject) VALUES('{account}','market-test','{account}');"
                f"INSERT INTO game.characters(character_id,account_id,world_id,name,creation_id,race,gender) VALUES('{character}','{account}',{world},'{character_name}',1,1,1);", database=name)
        env = os.environ.copy()
        env.update(AION_TEST_DATABASE=f'Host=127.0.0.1;Port={PORT};Database={name};Username=aion_app;Password={credentials["aion_app"]};Maximum Pool Size=4;Timeout=5;Command Timeout=10',
                   AION_TEST_ACCOUNTS=','.join(accounts), AION_TEST_CHARACTERS=','.join(characters))
        result = subprocess.run(['dotnet', str(assembly)], env=env, cwd=ROOT, capture_output=True, text=True, encoding='utf-8', timeout=120)
        output = result.stdout + result.stderr
        for password in credentials.values():
            output = output.replace(password, '[redacted]')
        print(output.strip())
        if result.returncode:
            raise RuntimeError('Market checks failed')
    finally:
        sql(f'DROP DATABASE {name} WITH (FORCE);', database='postgres')
    print('Disposable test database removed; development data unchanged.')


if __name__ == '__main__':
    main()
