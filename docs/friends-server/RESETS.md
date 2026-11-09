# Resets: one clock for the whole server

Every daily, weekly and monthly reset on the server follows one clock, `Aion:Resets` (`server/Aion.World/ResetClock.cs`).
This is our own setting (OURS). The official server resets at 07:00 UTC (16:00 KST), with weekly resets on Wednesday.

| Key | Default | Meaning |
|---|---|---|
| `Aion__Resets__Hour` | `5` | the hour (0-23) every reset happens |
| `Aion__Resets__WeeklyDay` | `Wednesday` | the day the weekly resets happen, at that hour (same day as the official server) |
| `Aion__Resets__TimeZone` | empty | the time zone the hour is read in. Empty means the host's own zone. Otherwise give an IANA id (`America/Chicago`) or a Windows id (`Central Standard Time`) |

The default is **every day at 05:00, weekly on Wednesday at 05:00, host local time**. A month starts on day 1 at 05:00.
The host logs the clock it uses at startup:

```bash
journalctl -u aion-host | grep "World rates" | tail -1    # ... Resets: daily at 05:00, weekly on Wednesday (host zone ...)
```

## What follows the clock

| What | Before | Now |
|---|---|---|
| Daily missions (achievements with reset 1) | UTC midnight | the reset day |
| Weekly missions (reset 2) | Monday, UTC midnight | the reset week (Wednesday 05:00) |
| Attendance check-in (one per day) | UTC midnight | the reset day |
| Battle pass "week ends" time sent to the client | the next Monday, UTC midnight | the next weekly reset |
| Shop purchase limits (daily / weekly / monthly) | 07:00 UTC, weekly Wednesday, monthly day 1 | the reset day / week / month |
| Period collection book (the daily and weekly collections) | 24-hour steps from the season start (07:00 UTC) | the reset day / week, counted from the season's first day |
| Dungeon entry counts (`Aion:Dungeons`, when `EntryLimits` is on) | 09:00 host time | the reset day. `Aion__Dungeons__ResetHour` still works: if you set it, it changes the hour for entry counts only |

Daily and weekly missions now also start over while a player is online. Before, they started over only at the next
login. A player who is online at 05:00 starts from 0 on the next count. Rewards from yesterday that were not taken
are gone, which is also what happens at login.

Not covered: the attendance boards' own open windows, the battle pass and season date windows, and the collection
season's start and end. These are fixed dates from the client's data and are not resets. The collection data's
`reset_hour_utc` (7) is no longer read.

## Setting it

On the WSL install (written into `/etc/aion/game.env`, other lines are kept):

```bash
sudo deploy/windows/wsl-install.sh --rates-only --reset-hour 5 --reset-day Wednesday
# optional, only if WSL's zone is not yours (see below):
sudo deploy/windows/wsl-install.sh --rates-only --reset-timezone America/Chicago
```

On a plain Linux install, `tools/server_env.py` has the same flags (`--reset-hour`, `--reset-day`,
`--reset-timezone`). You can also add the `Aion__Resets__*` lines to `/etc/aion/game.env` yourself and run
`sudo systemctl restart aion-host`. A bad value (hour 24, an unknown zone) stops the host at startup with a message
that names the key.

## WSL2: check the time zone

With `TimeZone` left empty, "05:00" means 05:00 in **WSL's** time zone, not Windows'. WSL2 Ubuntu often starts on UTC,
or keeps the zone from when it was installed. Check it inside WSL:

```bash
timedatectl              # "Time zone: America/Chicago (CST, -0600)"
ls -l /etc/localtime     # -> /usr/share/zoneinfo/America/Chicago
date                     # should show the same clock as Windows
```

If it is not your zone, fix one of these two (either works):

- set WSL's zone: `sudo timedatectl set-timezone America/Chicago` (use your own zone; `timedatectl list-timezones`
  lists them), then `sudo systemctl restart aion-host`; or
- leave WSL alone and set `Aion__Resets__TimeZone` (`--reset-timezone America/Chicago`).

The clock follows daylight saving time: the reset stays at 05:00 local time all year, which is 10:00 or 11:00 UTC for
US Central time. A day that skips 05:00 resets at the next valid time. A day that repeats it resets the first time.

## Changing the setting on a running server

Changing the hour, the day or the zone moves the current day or week one time. Stored counts carry a period label:
the shop purchases and the dungeon entries use the day (`2026-11-09`), the week (`w2026-11-04`) or the month
(`2026-11`). Mission states carry the time they were last updated. Right after a change, the current period can get
a new label, which resets those counts early. It can also keep yesterday's label for a few hours, so counts already
used today stay used until the new reset. This happens only once, and the next reset is normal. This change itself
(05:00 local and Wednesday weeks, replacing 07:00 UTC for the shop, UTC midnight and Monday weeks for missions, 09:00
for dungeons) has the same one-time effect when it is first deployed.

## Checks

`tests/Aion.Native.Checks` ("reset clock checks") pins a UTC clock (`ResetClock.Utc()`), so the checks give the same
result on any machine's zone. They cover the day, week and month boundaries, the settings and the refused values,
the daylight-saving changes (America/New_York, when the machine has the time zone database), the dungeon override,
the mission cycles, the shop periods and the collection book.
