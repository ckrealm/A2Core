"""Write the environment file for a hosted server (systemd EnvironmentFile; deploy/linux).

Reads the database credentials made by `postgres_dev.py setup` (AION_PG_HOME) and the
server data directory unpacked from `pack_server_data.py`, and prints KEY=VALUE lines:
database connections, bind/public addresses, HTTPS, the native ports, chat, the platform
bootstrap, the NCGuard sink and, when given, the rates and host rules. Contains passwords: write it to a file only root and the
service account can read.
"""
import argparse
import ipaddress
import json
import secrets
import string
from pathlib import Path
import sys

try:
    from .postgres_dev import HOME, PORT
except ImportError:
    from postgres_dev import HOME, PORT

PORTS = {'account': 8780, 'bootstrap': 8087, 'login': 3700, 'game': 13328, 'chat': 8091, 'ncguard': 443}


def connection(role, password, pool):
    # Generated passwords are URL-safe, so the value needs no quoting (EnvironmentFile is not a shell).
    return (f'Host=127.0.0.1;Port={PORT};Database=aion;Username={role};Password={password};'
            f'Maximum Pool Size={pool};Timeout=5;Command Timeout=15')


def geolocation(text):
    """Our GET geolocations/v1.0/me body (same layout as run_game_chain.geolocation_response)."""
    lat, lon, country = text.split(',')
    lat, lon, country = float(lat), float(lon), country.strip().upper()
    if not (-90 <= lat <= 90 and -180 <= lon <= 180 and len(country) == 2 and country.isalpha()):
        raise ValueError('--geolocation must be LAT,LON,CC')
    return {'geolocation': country, 'coordinates': {'latitude': f'{lat:.4f}', 'longitude': f'{lon:.4f}'}}


def environment(args, credentials):
    data = args.data.resolve()
    required = {'bootstrap keys': data / 'bootstrap/keys/app-key.json',
                'policy': data / 'bootstrap/policy/app-policy.json',
                'NCGuard fixture': data / 'ncguard/fixture.json'}
    missing = [f'{name} ({path})' for name, path in required.items() if not path.exists()]
    if missing:
        raise ValueError('Server data incomplete: ' + ', '.join(missing))
    env = {
        'ASPNETCORE_ENVIRONMENT': 'Production',
        'ConnectionStrings__Aion': connection('aion_app', credentials['aion_app'], 32),
        'ConnectionStrings__Auth': connection('aion_auth', credentials['aion_auth'], 16),
        'Aion__LocalPort': PORTS['account'],
        # Closed by default: admins create accounts on the dashboard.
        'Aion__Accounts__Registration': 'true' if getattr(args, 'open_registration', False) else 'false',
        'Aion__Network__BindAddress': args.bind,
        'Aion__Network__PublicAddress': args.public_address,
        'Aion__Native__LoginPort': PORTS['login'],
        'Aion__Native__GamePort': PORTS['game'],
        'Aion__Native__Logins': 'accounts',
        'Aion__Native__MaxConnections': args.max_connections,
        'Aion__ChatPort': PORTS['chat'],
        'Aion__Platform__Port': PORTS['bootstrap'],
        'Aion__Platform__FixtureDirectory': data / 'bootstrap/keys',
        'Aion__Platform__PolicyFile': data / 'bootstrap/policy/app-policy.json',
        # Policy custom_data switches of the game (its key table, read from the client 2026-10-18). The title screen's
        # Security Settings button is governed by SecondaryAuthCheckType (value CANDIDATE until a launch confirms).
        # SteamShowOnlySteamLogin=true is NOT set: it moves the client to the Steam token login route
        # (sessions/v1.0/login_sessions;token); the bootstrap answers it with the login session object, which the client
        # rejected on 2026-10-18 (200, then "failed to login"), so the reply shape is still unknown.
        'Aion__Platform__CustomData__SecondaryAuthCheckType': 'None',
        'Aion__NcGuard__Port': PORTS['ncguard'],
        'Aion__NcGuard__FixtureFile': data / 'ncguard/fixture.json',
        'Aion__NcGuard__DataDirectory': args.state / 'ncguard',
        # Players' anti-cheat requests are answered, never recorded.
        'Aion__NcGuard__Capture': 'false',
    }
    if (data / 'terrain').is_dir():
        env['Aion__Native__TerrainDirectory'] = data / 'terrain'
    if (data / 'navmesh').is_dir():
        env['Aion__Native__NavMeshDirectory'] = data / 'navmesh'
    # Player downloads (the launcher exe), served at /download/.
    env['Aion__Web__Downloads'] = args.state / 'downloads'
    # Dashboard icons and 3D preview (staff-only on a public host), when the export was unpacked.
    if (data / 'wardrobe/wardrobe.json').exists():
        env['Aion__WardrobePath'] = data / 'wardrobe'
    # The game's world-map paintings for the dashboard's live map (tools/MapImageExport: <id>.webp and maps.json).
    if (data / 'map-images/maps.json').exists():
        env['Aion__MapImagesPath'] = data / 'map-images'
    # Dashboard backups (Server page): pg_dump through postgres_dev.py into the state folder update.sh uses too.
    env['Aion__Backups__Directory'] = args.state / 'backups'
    env['Aion__Backups__Tool'] = Path(__file__).resolve().parent / 'postgres_dev.py'
    env['Aion__Backups__PgHome'] = args.state / 'postgres'
    if args.geolocation != 'off':
        env['Aion__Platform__GeolocationFile'] = args.state / 'geolocation-response.json'
    if args.proxy:
        env['Aion__Https__Proxy'] = 'true'
    else:
        env['Aion__Https__CertificateFile'] = args.certificate.resolve()
        env['Aion__Https__KeyFile'] = args.key.resolve()
    # The staff dashboard's private address; kept across updates (install.sh passes the current one).
    env['Aion__Web__StaffPath'] = getattr(args, 'staff_path', None) or '/' + ''.join(secrets.choice(string.ascii_lowercase + string.digits) for _ in range(16))
    for index, map_id in enumerate(args.preload):
        env[f'Aion__Native__PreloadMaps__{index}'] = map_id
    env.update(gameplay(args))
    return env


# Rates and host switches (OURS: the server owner's choice). Each is written only when given, so the host's defaults
# (friends rates, PvP on, 30 s respawn, round-robin party loot, fallback loot on, no player cap, no dungeon entry limits,
# untracked objectives done after 30 s, no time gates, every skin, pet and wing, crafts always succeed, pets at half the souls) apply otherwise.
GAMEPLAY = {
    'rates_profile': 'Aion__Rates__Profile',
    'exp_rate': 'Aion__Rates__Experience',
    'quest_exp_rate': 'Aion__Rates__QuestExperience',
    'kinah_rate': 'Aion__Rates__Kinah',
    'drop_rate': 'Aion__Rates__Drop',
    'kinah_chance': 'Aion__Rates__KinahChance',
    'gather_exp_rate': 'Aion__Rates__GatherExperience',
    'craft_exp_rate': 'Aion__Rates__CraftExperience',
    'pet_exp_rate': 'Aion__Rates__PetExperience',
    'shop_limits': 'Aion__TimeGates__ShopLimits',
    'dungeon_timers': 'Aion__TimeGates__DungeonTimeLimits',
    'collection_rotation': 'Aion__TimeGates__CollectionRotation',
    'gather_respawn_seconds': 'Aion__TimeGates__GatherRespawnSeconds',
    'all_skins': 'Aion__Unlocks__AllSkins',
    'all_pets': 'Aion__Unlocks__AllPets',
    'all_wings': 'Aion__Unlocks__AllWings',
    'revive_seconds': 'Aion__TimeGates__ReviveSeconds',
    'boost': 'Aion__Boost__Enabled',
    'boost_level': 'Aion__Boost__Level',
    'boost_price': 'Aion__Boost__Price',
    'craft_always_succeeds': 'Aion__Unlocks__CraftAlwaysSucceeds',
    'pet_soul_cost': 'Aion__Unlocks__PetSoulCost',
    'max_players': 'Aion__Native__MaxPlayers',
    'respawn_seconds': 'Aion__Rules__RespawnSeconds',
    'pvp': 'Aion__Rules__Pvp',
    'party_loot': 'Aion__Rules__PartyLoot',
    'fallback_loot': 'Aion__Rules__FallbackLoot',
    'dungeon_limits': 'Aion__Dungeons__EntryLimits',
    'dungeon_entries': 'Aion__Dungeons__DailyEntries',
    'dungeon_reset_hour': 'Aion__Dungeons__ResetHour',
    'untracked_objective_seconds': 'Aion__Dungeons__UntrackedObjectiveSeconds',
}


SWITCHES = ('pvp', 'fallback_loot', 'dungeon_limits', 'shop_limits', 'dungeon_timers', 'collection_rotation',
            'all_skins', 'all_pets', 'all_wings', 'craft_always_succeeds', 'boost')


def gameplay(args):
    env = {}
    for name, key in GAMEPLAY.items():
        value = getattr(args, name, None)
        if value is None:
            continue
        if name in SWITCHES:
            value = 'true' if value == 'on' else 'false'
        env[key] = value
    # Per-dungeon daily entries (Dungeon.dat id or name = count, 0 no limit): Aion:Dungeons:Entries:<dungeon>.
    for dungeon, count in getattr(args, 'dungeon_entry', None) or []:
        env[f'Aion__Dungeons__Entries__{dungeon}'] = count
    return env


def dungeon_entry(text):
    name, _, count = text.partition('=')
    if not name or not all(c.isalnum() or c in '_-' for c in name) or not count.isdigit() or int(count) > 1000:
        raise argparse.ArgumentTypeError('a dungeon entry override is <Dungeon.dat id or name>=<0-1000>')
    return name, int(count)


def rate(text):
    value = float(text)
    if not 0 <= value <= 100:
        raise argparse.ArgumentTypeError('a rate must be between 0 and 100')
    return value


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--public-address', required=True, help='IPv4 address players connect to')
    parser.add_argument('--bind', default='0.0.0.0', help='listen address (default every interface)')
    parser.add_argument('--data', type=Path, required=True, help='unpacked server data (pack_server_data.py)')
    parser.add_argument('--state', type=Path, required=True, help='writable service directory (NCGuard state, geolocation)')
    tls = parser.add_mutually_exclusive_group(required=True)
    tls.add_argument('--proxy', action='store_true', help='a TLS proxy on this machine serves the account site')
    tls.add_argument('--certificate', type=Path, help='PEM certificate for the account port (with --key)')
    parser.add_argument('--key', type=Path, help='PEM private key for --certificate')
    parser.add_argument('--open-registration', action='store_true', help='let anyone create an account on the account page')
    parser.add_argument('--staff-path', help='staff dashboard address, e.g. /k3x9... (default: a new random one)')
    parser.add_argument('--max-connections', type=int, default=200, help='per native port')
    parser.add_argument('--preload', type=int, nargs='*', default=[1000], help='maps loaded at startup')
    rates = parser.add_argument_group('rates and rules (unset: the host defaults)')
    rates.add_argument('--rates-profile', choices=['friends', 'official', 'accelerated'],
                       help='friends (the host default): experience 10, Kinah 10, Kinah chance 2, drops 3, professions and pets 5; '
                            'official: all rates 1; accelerated: experience 5, Kinah 3, drops 3 (explicit rates override)')
    rates.add_argument('--exp-rate', type=rate, help='kill experience multiplier')
    rates.add_argument('--quest-exp-rate', type=rate, help='quest experience multiplier (default: the experience rate)')
    rates.add_argument('--kinah-rate', type=rate, help='Kinah gained from quests, drops, chests and rewards')
    rates.add_argument('--drop-rate', type=rate, help='loot table rolls per kill')
    rates.add_argument('--kinah-chance', type=rate, help="multiplies a monster's chance to drop Kinah (capped at certain)")
    rates.add_argument('--gather-exp-rate', type=rate, help='gathering mastery experience multiplier')
    rates.add_argument('--craft-exp-rate', type=rate, help='crafting mastery experience multiplier')
    rates.add_argument('--pet-exp-rate', type=rate, help='pet (creature board) experience multiplier')
    rates.add_argument('--max-players', type=int, help='refuse logins beyond this many players (0: no cap)')
    rates.add_argument('--respawn-seconds', type=int, help='monster respawn after a kill (5-3600, default 30)')
    rates.add_argument('--pvp', choices=['on', 'off'], help='duels, arenas and Abyss player damage (default on)')
    rates.add_argument('--party-loot', choices=['freeforall', 'roundrobin', 'killer'],
                       help="a party member's kill: items to a random member nearby, to members in turn (default), or all to the killer")
    rates.add_argument('--fallback-loot', choices=['on', 'off'],
                       help='monsters without a reward table drop generic items of their level (default on)')
    dungeons = parser.add_argument_group('dungeons (unset: the host defaults, entry limits off)')
    dungeons.add_argument('--dungeon-limits', choices=['on', 'off'],
                          help='daily entry limits for expeditions, awakening, daily dungeons and raids (default off)')
    dungeons.add_argument('--dungeon-entries', type=int, help='entries a day per limited dungeon (default 3; 0 no limit)')
    dungeons.add_argument('--dungeon-entry', type=dungeon_entry, action='append', metavar='DUNGEON=N',
                          help='entries a day for one dungeon (Dungeon.dat id or name; 0 no limit); repeatable')
    dungeons.add_argument('--dungeon-reset-hour', type=int, help='server local hour the daily counts reset (0-23, default 9)')
    dungeons.add_argument('--untracked-objective-seconds', type=int,
                          help='map-event objectives the server cannot follow count as done after this long (1-3600, default 30)')
    gates = parser.add_argument_group('time gates (unset: the host defaults, all off)')
    gates.add_argument('--shop-limits', choices=['on', 'off'], help="merchants' daily/weekly/monthly/one-time purchase caps")
    gates.add_argument('--dungeon-timers', choices=['on', 'off'], help='timed dungeons fail when their timer runs out')
    gates.add_argument('--collection-rotation', choices=['on', 'off'], help='daily/weekly collections open one day or week at a time')
    gates.add_argument('--gather-respawn-seconds', type=int, help='a gathered node is back for that player after this long (0-3600, default 10)')
    unlocks = parser.add_argument_group('unlocks (unset: the host defaults)')
    unlocks.add_argument('--all-skins', choices=['on', 'off'], help='every character gets every skin at its next world entry (default on)')
    unlocks.add_argument('--all-pets', choices=['on', 'off'], help='every character gets every pet at its top level, boards maxed (default on)')
    unlocks.add_argument('--all-wings', choices=['on', 'off'], help='every character gets every wing of its race (default on)')
    gates.add_argument('--revive-seconds', type=int, help='a dead player is revived at the respawn point after this long (1-600, default 5)')
    unlocks.add_argument('--boost', choices=['on', 'off'], help='//boost alt boost (default on)')
    unlocks.add_argument('--boost-level', type=int, help='the level //boost gives (default 45; also the level the account needs)')
    unlocks.add_argument('--boost-price', type=int, help='what //boost costs in Kinah (default 10000000)')
    unlocks.add_argument('--craft-always-succeeds', choices=['on', 'off'], help='crafts never fail (default on)')
    unlocks.add_argument('--pet-soul-cost', type=float, help='multiplies the souls pets cost to acquire and level (0-10, default 0.5)')
    parser.add_argument('--geolocation', default='59.9139,10.7522,NO', metavar='LAT,LON,CC',
                        help="server location for the region screen ('off' to omit)")
    args = parser.parse_args(argv)
    try:
        address = ipaddress.ip_address(args.public_address)
    except ValueError:
        parser.error('--public-address must be an IPv4 address')
    if address.version != 4 or address.is_loopback:
        parser.error('--public-address must be a non-loopback IPv4 address')
    if args.certificate and not args.key:
        parser.error('--certificate needs --key')
    if args.max_players is not None and args.max_players < 0:
        parser.error('--max-players must be 0 or more')
    if args.respawn_seconds is not None and not 5 <= args.respawn_seconds <= 3600:
        parser.error('--respawn-seconds must be between 5 and 3600')
    if args.dungeon_entries is not None and not 0 <= args.dungeon_entries <= 1000:
        parser.error('--dungeon-entries must be between 0 and 1000')
    if args.dungeon_reset_hour is not None and not 0 <= args.dungeon_reset_hour <= 23:
        parser.error('--dungeon-reset-hour must be between 0 and 23')
    if args.gather_respawn_seconds is not None and not 0 <= args.gather_respawn_seconds <= 3600:
        parser.error('--gather-respawn-seconds must be between 0 and 3600')
    if args.revive_seconds is not None and not 1 <= args.revive_seconds <= 600:
        parser.error('--revive-seconds must be between 1 and 600')
    if args.boost_level is not None and not 2 <= args.boost_level <= 100:
        parser.error('--boost-level must be between 2 and 100')
    if args.boost_price is not None and not 0 <= args.boost_price <= 10_000_000_000:
        parser.error('--boost-price must be between 0 and 10000000000')
    if args.pet_soul_cost is not None and not 0 <= args.pet_soul_cost <= 10:
        parser.error('--pet-soul-cost must be between 0 and 10')
    if args.untracked_objective_seconds is not None and not 1 <= args.untracked_objective_seconds <= 3600:
        parser.error('--untracked-objective-seconds must be between 1 and 3600')
    if args.staff_path is not None and (len(args.staff_path) < 9 or not args.staff_path.startswith('/')
                                        or not all(c.isalnum() or c in '-_' for c in args.staff_path[1:])):
        parser.error('--staff-path must be / followed by at least 8 letters, digits, - or _')
    credentials = json.loads((HOME / 'credentials.json').read_text(encoding='utf-8'))
    try:
        env = environment(args, credentials)
        if args.geolocation != 'off':
            args.state.mkdir(parents=True, exist_ok=True)
            (args.state / 'geolocation-response.json').write_text(json.dumps(geolocation(args.geolocation)), encoding='utf-8')
    except ValueError as error:
        parser.error(str(error))
    sys.stdout.write(''.join(f'{key}={value}\n' for key, value in env.items()))
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
