"""Builds data/npcs-<lang>.json and data/skills-<lang>.json (en, de) from the cyberbadger6969/aion2-dps-meter data (GPL-3.0).
NPC list: every NPC (id -> name, boss flag, dungeon id, dummy flag). Skills: only NPC skill codes (1,000,000 - 9,999,999),
the range bosses use.
Run: python -I tools/make_data.py <path to aion2-dps-meter/data> <output data dir>"""
import json, os, sys

src, out = sys.argv[1], sys.argv[2]
nm = lambda v: v if isinstance(v, str) else (v or {}).get("name")


def npc(v):
    e = {"n": v["name"]}
    if v.get("isBoss"): e["b"] = 1
    if v.get("dungeonId"): e["d"] = v["dungeonId"]  # instance bosses only; field bosses have none
    if v.get("isDummy"): e["x"] = 1  # training dummies
    return e


for lang in ("en", "de"):
    npcs = json.load(open(os.path.join(src, "npcs", f"{lang}.json"), encoding="utf-8"))
    skills = json.load(open(os.path.join(src, "skills", f"{lang}.json"), encoding="utf-8"))
    n = {k: npc(v) for k, v in npcs.items() if v.get("name")}
    s = {k: nm(v) for k, v in skills.items() if k.isdigit() and 1_000_000 <= int(k) <= 9_999_999 and nm(v)}
    for name, data in ((f"npcs-{lang}.json", n), (f"skills-{lang}.json", s)):
        with open(os.path.join(out, name), "w", encoding="utf-8") as f:
            json.dump(data, f, ensure_ascii=False, separators=(",", ":"))
    print(lang, len(n), "npcs,", len(s), "npc skills")
