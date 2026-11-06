# Raid-Ansager für Aion 2

**English:** [README.md](README.md)

Der Raid-Ansager sagt in Dungeons und Raids an, was der Boss gerade macht. Die Ansage kommt per Sprache, mit Warnton
und als Text über dem Spiel. Dazu zeigt er Boss-HP und Kampfdauer an.

- **In jedem Dungeon** liest er die Fähigkeiten des Bosses mit ihrem Spielnamen vor, z. B. „Drana-Welle“ oder
  „Orbwurf“, sobald der Boss sie einsetzt. Jede Fähigkeit höchstens alle 20 Sekunden.
- **Für Bosse mit eigener Boss-Mod** (zuerst: Raid **Ludra**) kommen eigene Ansagen wie „Bingo, sicheres Feld
  suchen“, Phasen nach HP und Countdown-Balken („… in 5“).
- **Englisch oder Deutsch:** Standard ist Englisch. Umstellen per Rechtsklick auf das Symbol → **Language / Sprache →
  Deutsch**. Dann sind Ansagen, Fähigkeits- und Bossnamen, Menü und Overlay deutsch, und eine deutsche Stimme wird
  gewählt.

## So funktioniert es

Das Programm liest den Netzwerkverkehr von Aion 2 **nur mit**, so wie Wireshark. Daran erkennt es, welche Fähigkeit
der Boss einsetzt, wie viel HP er hat und wann der Kampf beginnt und endet. Es verändert nichts am Spiel, liest keinen
Speicher aus und drückt keine Tasten. Ins Internet geht es nur bei der Suche nach Updates auf GitHub (abschaltbar) und
beim Hochladen der Protokolle, wenn du es anklickst.

> **Achtung:** Laut einem anderen Tool-Autor stuft der NCSOFT-Support Software, die Spieldaten mitliest, als Verstoß
> gegen die Nutzungsbedingungen ein. Die Nutzung erfolgt **auf eigenes Risiko**.

## Installation

1. **Npcap** installieren: <https://npcap.com/#download> („Npcap … installer“). Den Installer mit den
   Standardoptionen ausführen. Das Häkchen „Restrict Npcap driver's access to Administrators only“ bleibt **aus**.
   Npcap braucht das Programm zum Mitlesen.
2. `RaidAnsager.exe` unter **Releases** herunterladen und in einen beliebigen Ordner legen, z. B. `Dokumente\RaidAnsager`.
   Eine Installation ist nicht nötig, .NET ist enthalten.
3. Doppelklicken. Falls Windows SmartScreen warnt („Der Computer wurde durch Windows geschützt“):
   **Weitere Informationen → Trotzdem ausführen**. Die Warnung kommt, weil die Datei nicht signiert ist.
4. Unten rechts im Infobereich erscheint ein Lautsprecher-Symbol. Fertig.

Aion 2 sollte im **Fenstermodus oder randlos** laufen. Im exklusiven Vollbild liegt das Overlay unter dem Spiel. Die
Sprachansagen funktionieren trotzdem.

## Bedienung

| Taste | Wirkung |
|---|---|
| **Strg+Alt+O** | Overlay verschieben (mit der Maus ziehen, dann noch einmal Strg+Alt+O) |
| **Strg+Alt+M** | Stumm an/aus |
| **Strg+Alt+T** | Test-Ansage |
| **Strg+Alt+K** | Letzte Boss-Attacke als wichtig markieren (siehe unten) |

**Rechtsklick** auf das Symbol öffnet das Menü:

- **Language / Sprache:** English oder Deutsch (Standard: English). Fehlt eine deutsche Stimme: Windows-Einstellungen →
  Zeit und Sprache → Sprache → Stimmen hinzufügen.
- **Meine Rolle:** Tank, Heiler oder DD. Ansagen, die nur eine andere Rolle betreffen, entfallen dann.
- **Alle Dungeon-Bosse (auch ohne Mod):** Ist das aus, reagiert der Ansager nur auf Bosse mit eigener Mod (Ludra,
  gelernte Timer).
- **Gelernte Countdown-Timer:** Countdowns, die aus den Protokollen gelernt wurden, bisher für 15 Dungeon-Bosse (z. B.
  Vollendete Berk „Furorwelle in 3“, Heiliger Auldor „Sturmstreich in 5“). Laut angesagt werden nur die wichtigen,
  die anderen laufen als stiller Balken.
- **Letzte Boss-Attacke als wichtig markieren** (Strg+Alt+K) und **Protokolle für die Timer hochladen:** siehe unten.
- **Unbekannte Boss-Fähigkeiten vorlesen:** Liest Fähigkeiten, für die es noch keine eigene Ansage gibt, mit ihrem
  Spielnamen vor. Wird es zu viel, ausschalten. Boss-HP und Overlay bleiben.
- **Stimme, Lautstärke, Sprechtempo, Overlay-Größe, Warntöne**
- **Raid-Mitschnitt speichern:** zeichnet nur in Raid-Zonen mit Mod auf (Ludra), damit die Ansagen verbessert werden
  können. Die Dateien bleiben auf deinem PC.
- **Kampfprotokolle öffnen:** Zu jedem Pull gibt es ein lesbares Protokoll mit Zeitleiste
- **Automatische Updates:** Das Programm sucht bei GitHub nach neuen Versionen und installiert sie selbst, aber nie
  während eines Boss-Kampfs oder in einem Dungeon. Danach startet es neu. Über „Nach Updates suchen“ geht das auch von
  Hand.
- **Beenden**

## Was angesagt wird

- In Dungeons ohne Mod: der Name jeder Boss-Fähigkeit in der gewählten Sprache, sobald der Boss sie beginnt. Normale Angriffe werden nicht
  angesagt. Oben im Overlay stehen Boss-Name, HP und Kampfzeit.
- Mit Mod (Ludra): ein Hinweis beim Pull, Phasen nach Boss-HP (z. B. Bingo bei etwa 50 % und 25 %), eigene Texte zu
  bekannten Fähigkeiten und Countdown-Balken für Fähigkeiten, die regelmäßig kommen
- „Boss besiegt“ am Ende

Wichtig: Zwischen dem Beginn einer Fähigkeit und dem Treffer liegen oft nur 0,5–2 Sekunden. Die Ansage hilft vor
allem bei Fähigkeiten mit längerer Ausholzeit und als Erinnerung, was gerade passiert. Was man bei einer Fähigkeit
tun muss, sagt er erst, wenn es für den Boss eine Mod gibt.

Die erste Ludra-Version stammt aus Guides und den Spieldaten. Nach den ersten Raidabenden wird sie anhand echter
Mitschnitte verbessert. Neue Versionen installieren sich automatisch.

## Beim Bau der Countdown-Timer helfen

Countdowns wie „Rückenattacke in 3“ lernt das Programm aus vielen Pulls von vielen Spielern. Eine Boss-Fähigkeit
bekommt einen Timer, wenn sie in mindestens 75 % der Fälle nach fester Abklingzeit kommt oder in festem Abstand nach
einer anderen Boss-Fähigkeit.

1. **Einfach spielen**, während der Ansager läuft. Jeder Dungeon- und Raid-Boss-Kampf wird automatisch protokolliert.
2. **Direkt nach einer wichtigen Attacke Strg+Alt+K drücken**, z. B. nach einer Rückenattacke, die du kommen sehen
   willst. Markierte Attacken bekommen zuerst einen Timer. Was jemanden getötet hat, zählt automatisch als wichtig.
3. **Protokolle hochladen:** Rechtsklick auf das Symbol → *Protokolle für die Timer hochladen*. Ein Klick genügt.
   Hochgeladen werden nur neue Protokolle, ohne Spielernamen. Ohne Internet geht es auch über *Protokolle als ZIP
   exportieren*, dann liegt die Datei auf dem Desktop.

Mit der nächsten Version kommen die gelernten Timer dann für alle.

## Mod-Update einspielen

Die neue `ludra.json` entweder **neben die RaidAnsager.exe in einen Ordner `mods`** legen, oder über das Menü
**Mods-Ordner öffnen** dorthin kopieren. Danach im Menü **Mods neu laden** wählen oder das Programm neu starten. Es gilt
immer die Datei mit der höchsten `version`.

## Wenn etwas nicht klappt

- **„Npcap fehlt“:** Npcap installieren (siehe oben) und das Programm neu starten.
- **Kein Ton:** Prüfen, ob „Stumm“ aktiv ist (Strg+Alt+M), dann mit Strg+Alt+T testen. Die Ausgabe läuft über das
  Windows-Standardgerät.
- **Kein Overlay im Spiel:** Aion 2 auf Fenstermodus oder randlos stellen.
- **„Kein Netzwerkadapter mit Spielverbindung“:** Das kann an einem VPN liegen.
- Fehler stehen in `%APPDATA%\RaidAnsager\fehler.log`. Die Datei bitte an den Raidleiter schicken.
