# Boss-Mods: Format

Eine Boss-Mod ist eine JSON-Datei (Kommentare mit `//` sind erlaubt). Eingebaut ist `mods/ludra.json`. Eigene oder
neuere Dateien liegen in `%APPDATA%\RaidAnsager\mods` oder in `mods\` neben der EXE. Pro `id` gilt die höchste
`version`. Bei Gleichstand gewinnt die Datei gegen die eingebaute Fassung. `RaidAnsager pruefen` meldet Fehler.

```jsonc
{
  "id": "ludra",                  // eindeutig; gleiche id = neuere Fassung derselben Mod
  "name": "Weihestätte: Ewiger Ludra",
  "version": "2026-11-08",        // sortierbar; höher = gewinnt
  "zones": [600082],              // nur in diesen Zonen aktiv (leer = überall)
  "bosses": [
    {
      "id": "rahu",
      "name": "Rahu der Isolation",
      "npc": [2301006],           // NPC-Codes des Bosses
      "resetAfter": 45,           // Sekunden ohne Kampf → Reset/Wipe
      "onKill": "{boss} besiegt", // "" = still
      "triggers": [ /* siehe unten */ ]
    }
  ]
}
```

## Trigger

| Feld | Bedeutung |
|---|---|
| `name` | Bezeichnung (erscheint im Protokoll) |
| `on` | `pull`, `cast`, `hit`, `hp`, `buff`, `spawn`, `death` |
| `skill` | Skill-IDs für `cast` (Beginn der Fähigkeit) und `hit` (Treffer) |
| `npc` | NPC-Codes für `spawn`/`death`; bei `cast`/`hit` nur, wenn dieser NPC wirkt (Standard: jeder NPC) |
| `buff` | Buff-IDs für `buff` |
| `below` | `hp`: einmal pro Pull, sobald der Boss unter diesen Prozentwert fällt |
| `target` | `any` (Standard), `me` (Ziel bist du), `notme`, `player`, `boss` |
| `minStack` | `buff`: erst ab so vielen Stapeln |
| `phase` | nur in dieser Phase aktiv (ein Pull beginnt mit Phase 1) |
| `setPhase` | setzt die Phase |
| `say` | gesprochener Text |
| `show` | Text im Overlay (Standard: wie `say`) |
| `color` | Farbe des Overlay-Texts, z. B. `#FF5050` |
| `sound` | `alarm` (unterbricht laufende Ansagen), `info`, `none` |
| `showSeconds` | Anzeigedauer im Overlay (Standard 4) |
| `delay` | Sekunden bis zur Ansage (z. B. „jetzt raus“ nach dem Ausholen) |
| `cooldown` | Sekunden ohne erneutes Auslösen (Standard 3; ein Cast auf mehrere Ziele schickt mehrere Pakete) |
| `once` | nur einmal pro Pull |
| `roles` | nur für diese Rollen: `tank`, `heiler`, `dd` |
| `bar` | Countdown-Balken: `{ "text": "Nächste Bombe", "in": 30, "warn": 5, "warnSay": "{text} in {warn}", "endSay": "" }` |
| `cancelBar` | entfernt den Balken mit diesem Text |
| `verified` | `false` = Skill-ID noch nicht im eigenen Mitschnitt gesehen. Nur geprüfte Trigger erkennen einen Boss beim Einstieg mitten im Kampf |
| `note` | Notiz, wird nicht angesagt |

**Sprachen:** `say`, `show`, `onKill` und die Texte in `bar` dürfen ein einfacher Text sein (gilt für jede Sprache) oder
ein Objekt je Sprache: `"say": { "en": "Spread out", "de": "Verteilen" }`. Fehlt die eingestellte Sprache, wird der
einfache Text, dann Englisch genommen. Bossnamen kommen automatisch aus den Spieldaten der eingestellten Sprache, `name`
ist nur die Rückfalllösung. Der Mod-Entwurf aus `analyse` schreibt die Fähigkeitsnamen gleich in beiden Sprachen.

Platzhalter in `say`/`show`/`bar.text`: `{boss}`, `{skill}` (Spielname der Fähigkeit), `{target}` (Name des Ziels oder
„dich“), `{stack}`, `{hp}`, `{npc}`, `{phase}`.

## Beispiele

```jsonc
// Ansage beim Cast und Balken bis zum nächsten Einsatz
{ "name": "Purpurbombe", "on": "cast", "skill": [1803010], "say": "Bombe auf {target}", "sound": "alarm",
  "bar": { "text": "Nächste Bombe", "in": 30, "warn": 5 } }

// Debuff auf dir ab 2 Stapeln
{ "name": "Gift", "on": "buff", "buff": [123456], "target": "me", "minStack": 2, "say": "Zwei Stapel, raus", "sound": "alarm" }

// Phasenwechsel bei 50 %, danach andere Trigger mit "phase": 2
{ "name": "Phase 2", "on": "hp", "below": 50, "setPhase": 2, "say": "Phase zwei" }

// erst 2 Sekunden nach Beginn der Fähigkeit "jetzt springen"
{ "name": "Welle", "on": "cast", "skill": [1800060], "delay": 2, "say": "Jetzt springen", "sound": "alarm" }
```

## Countdown-Timer aus vielen Protokollen

**Abholen:** Die Mitglieder laden ihre Protokolle über das Menü hoch, an den Upload-Dienst `server/upload-worker`.
`RaidAnsager abholen` lädt alle neuen Uploads nach `%APPDATA%\RaidAnsager\uploads` und startet gleich die Auswertung
über diese Uploads und die eigenen Protokolle. `--loeschen` entfernt die abgeholten Uploads vom Dienst, `--ohne-analyse`
lädt nur herunter. Der Admin-Schlüssel liegt in `%APPDATA%\RaidAnsager\admin-token.txt` und gehört nie ins Repo.

`RaidAnsager analyse` nimmt neben Mitschnitten auch Kampfprotokolle (`.jsonl`), ganze Ordner und die ZIP-Exporte der
Mitglieder. Derselbe Pull von mehreren Mitgliedern (gleicher Boss, Start ±15 s) zählt einmal. Für jede Boss-Fähigkeit
wird gemessen:

- **Abklingzeit:** Abstand zwischen zwei Einsätzen,
- **nach dem Pull:** Zeitpunkt des ersten Einsatzes,
- **nach Fähigkeit Y:** fester Abstand nach einer anderen Boss-Fähigkeit. Bewertet wird er mit dem kleineren der
  Werte „wie viele X-Einsätze erklärt er“ und „wie oft folgt auf Y wirklich X“.

Ein Timer entsteht ab 4 Beispielen, 75 % Treffern (±1 s bzw. ±12 %) und 3 s Vorwarnung. Vorrang haben markierte
Fähigkeiten (Strg+Alt+K, `MARK`), dann tödliche (letzter Treffer vor einem `TOD`), dann häufig treffende (`HITME`).
`vorhersagen.txt` listet alle Fähigkeiten mit den drei Werten, `dungeon-timers.json` ist die fertige Mod (höchstens 6
Timer pro Boss). Prüfen, Texte anpassen, in `mods/` legen und mit dem nächsten Release ausliefern.

## Woher die IDs kommen

`RaidAnsager analyse <mitschnitt.pcapng>` schreibt je Pull ein Protokoll mit allen Casts (Skill-ID + Spielname), Buffs
(Buff-ID, Quelle), Adds (NPC-Code), HP-Verlauf und Toden. Dazu entsteht ein Mod-Entwurf (`entwurf_zone<Zone>.json`)
mit einem Trigger pro Boss-Fähigkeit. Regelmäßige Fähigkeiten bekommen dort bereits einen Balken mit dem gemessenen
Abstand. Den Entwurf kürzen, Ansagetexte schreiben, `version` erhöhen, fertig. Ob die Mod wie gewünscht klingt, zeigt
`RaidAnsager vorschau <mitschnitt> --mod <datei>`: Es schreibt `ansagen.txt` und Overlay-Bilder, ganz ohne Ton.
