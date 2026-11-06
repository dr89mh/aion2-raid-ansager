# Raid Announcer for Aion 2

**Deutsch:** [LIESMICH.md](LIESMICH.md)

Calls out what the boss is doing in **dungeons and raids**: by voice, warning tone and text on top of the game, with
boss HP and countdown bars for skills that come at regular intervals. It only reads the network traffic of Aion 2
(Npcap, like Wireshark) and changes nothing in the game.

![Overlay with boss HP, callout and countdown](docs/overlay.png)

- **Every dungeon boss:** its skills are read out by their in-game name as soon as it starts them
  (e.g. "Drana Wave", "Explosive Crystal").
- **Bosses with a boss mod**, first of all the raid **Ludra**: own callouts, phases by HP, countdowns ("… in 5"),
  callouts per role (tank, healer, DPS).
- **English or German** (menu "Language / Sprache"): callouts, skill and boss names, menu and overlay. English is the
  default.
- **Learned countdowns** for 15 dungeon bosses so far, e.g. Ultimate Berk "Wave of Fury in 3" (38 of 38 times
  8.4 s after its wind-up), Divine Auldor "Tempest Strike in 5", Watchdog Kwapo "Thunder Cry". Only the important ones
  are spoken, the others are silent bars.
- A fight log for every pull, which new boss mods and timers are built from.

> **Warning:** According to another tool author, NCSOFT support considers software that reads game data a violation of
> the terms of service. **Use at your own risk.**

## Download

1. Install [**Npcap**](https://npcap.com/#download) (default options; leave "Restrict Npcap driver's access to
   Administrators only" unticked).
2. Download `RaidAnsager.exe` from [**Releases**](../../releases/latest) and start it. No installation, .NET is
   included. If SmartScreen warns: *More info → Run anyway* (the file is not signed).
3. Right-click the speaker icon at the bottom right for the settings.
   **Ctrl+Alt+O** moves the overlay, **Ctrl+Alt+M** mutes, **Ctrl+Alt+T** plays a test callout,
   **Ctrl+Alt+K** marks the last boss attack as important (see below).

Play Aion 2 in windowed or borderless mode, otherwise the overlay stays behind the game (the voice still works).

## Settings (right-click on the icon)

| Item | Effect |
|---|---|
| Language / Sprache | English or Deutsch; a voice of that language is picked automatically |
| My role | Tank, healer or DPS: callouts meant only for another role are skipped |
| All dungeon bosses (also without a mod) | off = only bosses with a mod (Ludra, learned timers) |
| Learned countdown timers | the timers learned from member logs (mod `dungeon-timers`); off = learning mode only |
| Mark the last boss attack as important (Ctrl+Alt+K) | see "Help build countdown timers" |
| Export logs for the timers | zip on the desktop for your raid lead, no player names |
| Read out unknown boss skills | the learning mode; turn it off if it talks too much (HP and overlay stay) |
| Voice, volume, speech rate, overlay size, warning tones | |
| Save raid recordings | records only in raid zones with a mod, so the callouts can be improved; files stay on your PC |
| Open fight logs | a readable log with timeline for every pull |

If something goes wrong, the file `%APPDATA%\RaidAnsager\fehler.log` helps (paste the path into the Explorer address
bar).

## Help build countdown timers

Countdowns like "Backstab in 3" are learned from many pulls by many players: a boss skill gets a timer when it comes
at a fixed cooldown or at a fixed gap after another boss skill, in at least 75 % of the recorded casts.

1. **Just play** with the announcer running. Every dungeon and raid boss fight is logged automatically.
2. **Press Ctrl+Alt+K right after an attack that matters** (e.g. a back attack you want to see coming). Marked attacks
   get a timer first. Attacks that killed someone count as important automatically.
3. **Send the logs:** right-click the icon → *Export logs for the timers*. A zip appears on your desktop; send it to
   your raid lead. It contains no player names.

The raid lead runs `RaidAnsager analyse <all zips or folders>`: the same pull from several members counts once, and the
result is `vorhersagen.txt` (what can be predicted, and how reliably) plus `dungeon-timers.json`, a mod with the
timers for the next release.

## Boss mods

How callouts for a boss are written is described in [MODS.md](MODS.md) (German). The built-in Ludra mod is
[`mods/ludra.json`](mods/ludra.json). Texts can be given per language (`{ "en": "…", "de": "…" }`). Its HP thresholds
still come from guides for the old version and will be reworked from real recordings after the first raid nights.

## Building

```
git clone --recurse-submodules https://github.com/dr89mh/aion2-raid-ansager.git
cd aion2-raid-ansager
dotnet build -c Release     # bin\Release\net10.0-windows\RaidAnsager.exe
publish.cmd                 # dist\RaidAnsager.exe (single file, .NET included)
```

Requires the .NET 10 SDK. Capture, TCP and framing come from the core of
[Cube Radar](https://github.com/dr89mh/aion2-cube-radar) (submodule `external/aion2-cube-radar`).

Tools for analysis (need Npcap; `--lang en|de` sets the language for one run):

| Command | Effect |
|---|---|
| `RaidAnsager analyse <recording.pcapng / log.jsonl / folder / export.zip …> [--out folder]` | boss fights: skill table (gaps, HP, lead time), countdown predictions, timer mod, draft mod |
| `RaidAnsager export` | zip of all fight logs on the desktop (same as the menu item) |
| `RaidAnsager replay <recording.pcapng> [--speed 4]` | play a recording with overlay and voice |
| `RaidAnsager vorschau <recording.pcapng> [--mod file] [--alle]` | no sound: list of callouts and overlay images |
| `RaidAnsager pruefen` | load and check the boss mods |

| Path | Content |
|---|---|
| `Protocol/CombatDecoder.cs` | frames → events: cast `02 38`, hit `04 38`/`05 38`, HP `00 8D`, buff `2A 38`/`2B 38`, spawn `41 36`, death `04 8D`, zone `01 40` |
| `Engine/Announcer.cs` | fight detection (pull, kill, reset), triggers, phases, bars, learning mode |
| `Engine/BossMod.cs`, `Engine/Lang.cs` | mod format and loading (built-in + folders, highest version wins), languages |
| `Analysis/` | fight logs, statistics, draft mod, commands |
| `App/` | live/replay, overlay, voice, tray, settings |
| `data/` | English and German NPC and skill names, built with `tools/make_data.py` |

## License

GPL-3.0. NPC and skill data and parts of the protocol knowledge come from
[cyberbadger6969/aion2-dps-meter](https://github.com/cyberbadger6969/aion2-dps-meter) (GPL-3.0). Further packet layouts
come from HamMeter-Aion2 and aion2dps (TheLoop705). Game content and names belong to NCSOFT.
