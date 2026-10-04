"""The compatibility list players read: the 98 sweep mods, each with what AutoFix did, whether it loaded, and how it did in
a real match on the current game (profiles/b0..b9, run with `pilot.py matches`).

    python compat.py > ../../docs/COMPATIBILITY.md

ISSUES says what is known about a mod; SOURCES maps the error sources the pilot reports (namespaces) to packages, so a
mod with no errors in its batch's match can say so.
"""
import os, re, sys
from collections import defaultdict

HERE = os.path.dirname(os.path.abspath(__file__))
SWEEP = os.path.join(HERE, "..", "sweep-packages.tsv")
BATCHES = [f"b{i}" for i in range(10)]

# error source (as the pilot names it) -> package
SOURCES = {
    "CR": "XAngelMoonX-CR", "ClassesManagerReborn": "Root-Classes_Manager_Reborn", "CardBarPatch": "BossSloth-CardBarPatch",
    "WWC": "willuwontu-WillsWackyCards", "PCE": "Pykess-PCE", "CardsPlusPlugin": "willis81808-CardsPlus",
    "ItemShops": "willuwontu-ItemShops", "WeaponsManager": "RS_Mind-WeaponsManager", "CrimsonAura": "willis81808-Arcana",
    "HermitHandler": "willis81808-Arcana", "Infoholic": "Penial-Infoholic", "ZomC_Cards": "Zom_23-ZOMC",
    "Supcom2Cards": "Alphahex-Supcom2Cards", "RootCore": "Root-Root_Core", "LobbyImprovements": "RoundsModdingCommunity-LobbyImprovements",
    "StickFightMaps": "BossSloth-StickFightMaps", "WillsWackyManagers": "willuwontu-WillsWackyManagers",
}

# what is known, by package: (status, note). Status: ok (works), minor (works, something small), issue (part broken),
# broken, bench (only the test's AI players hit it)
ISSUES = {
    "XAngelMoonX-CR": ("minor", "Drive, Flex and Careen log errors in some moments (their own bugs, on the old game too); the cards work."),
    "Root-Classes_Manager_Reborn": ("minor", "After JACK is picked with no class mods installed, every pick-end hook throws (its own bug)."),
    "willuwontu-WillsWackyCards": ("minor", "Momentum's changing description doesn't update on the card."),
    "Pykess-PCE": ("minor", "The Random card's visual effect throws every frame while it's on screen; the card works."),
    "willis81808-Arcana": ("minor", "Crimson Aura throws now and then (a collider on the player layer with no player); same on the old game."),
    "BossSloth-StickFightMaps": ("broken", "Built for MapsExtended 0.9.5: its maps load without their objects and the round stalls. Same on the old game."),
    "RS_Mind-WeaponsManager": ("bench", "Throws for players with no controls, which only the test's AI players are."),
    "willuwontu-ItemShops": ("bench", "Throws for players with no controls, which only the test's AI players are."),
    "Alphahex-Supcom2Cards": ("minor", "Darkenoid's pick throws for the round's loser, who picks while dead; same on the old game."),
    "willuwontu-WillsWackyManagers": ("bench", "Throws once if a room is joined seconds after the menu appears (only the test is that fast)."),
    "Zom_23-ZOMC": ("minor", "Perseverance (Pristine)'s pick throws for the round's loser, who picks while dead; same on the old game. Double Vision logs an error once per pick (its own bug)."),
    "Root-Root_Core": ("minor", "In the Toggle Cards menu, Root's categories show as separate rows instead of folded under \"Root\" (UnboundLib 4's menu is built differently). The toggles work."),
    "Pykess-Map_Embiggener": ("ok", "On the current game its out-of-bounds camera left only the background on screen (fixed by the Runtime)."),
    "Penial-Infoholic": ("minor", "Throws once at the start of a match, before there is a player to show."),
    "RoundsModdingCommunity-LobbyImprovements": ("minor", "Throws once when a local match starts."),
}


def rows(path):
    if not os.path.exists(path): return []
    return [l.rstrip("\r\n").split("\t") for l in open(path, encoding="utf-8", newline="\n")][1:]


def main():
    sweep = [l.rstrip("\n").split("\t") for l in open(SWEEP, encoding="utf-8") if l.strip() and not l.startswith("#")]
    batches_of, autofix, ran, errsrc, unloaded = defaultdict(list), defaultdict(list), {}, defaultdict(set), defaultdict(list)
    # mods with a hand-made patch: the bench profiles get it from the Gale layer, players from AutoFix
    curated = {l.split("\t")[0].split("/")[0].rsplit("-", 1)[0] for l in open(os.path.join(HERE, "..", "..", "patches", "patches.tsv"), encoding="utf-8") if l.strip()}
    for b in BATCHES:
        prof = os.path.join(HERE, "profiles", b)
        if not os.path.isdir(prof): continue
        for name, _ in (l.rstrip("\n").split("\t") for l in open(os.path.join(prof, "packages.txt"))):
            batches_of[name].append(b)
        for r in (l.rstrip("\n").split("\t") for l in open(os.path.join(prof, "BepInEx", "cache", "rounds-port", "index.tsv"), encoding="utf-8")
                  if l.strip() and not l.startswith(("#", "key\t"))):
            if len(r) >= 6: autofix[r[0].replace("\\", "/").split("/")[0]].append((r[5], r[6] if len(r) > 6 else ""))
        cards = rows(os.path.join(prof, "run", "pilot", "cards.tsv"))
        ran[b] = sum(1 for c in cards if len(c) > 1 and not c[1].startswith("(not offered"))
        for e in rows(os.path.join(prof, "run", "pilot", "errors.tsv")):
            if len(e) == 2: errsrc[b].add(e[0].replace(" (patched)", ""))
        log = os.path.join(prof, "run", "LogOutput.log")
        if os.path.exists(log):
            for l in open(log, encoding="utf-8", errors="replace"):
                if "[Error  :   BepInEx]" in l or "[Warning:   BepInEx]" in l and ("Could not load" in l or "kipping" in l or "ncompatib" in l):
                    unloaded[b].append(l.strip())

    print("# Compatibility\n")
    print("The 98 most-downloaded Thunderstore mods with code (October 2026), on the current game with DuctTape's "
          "fixes. Each was installed in a batch of about ten, with their dependencies, and played in a 12-minute match "
          "between two of the game's AI players, offered the batch's cards in turn (25 to 50 picks per match, so in a "
          "big batch not every card came up). \"In the match\" is what happened there; a library has no cards of its "
          "own.\n")
    print("| Mod | Version | AutoFix | In the match | Notes |\n|---|---|---|---|---|")
    counts = defaultdict(int)
    for name, version in sweep:
        played_in = [b for b in batches_of.get(name, []) if ran.get(b, 0) >= 20]   # a match that got going, not one cut short
        res = autofix.get(name, [])
        kinds = {r for r, _ in res if r not in ("not-a-mod",)}
        manual = sum(int(m.group(1)) for _, n in res for m in [re.match(r"(\d+) MANUAL", n)] if m)
        fix = ("fixed" if "fixed" in kinds else "nothing to fix" if kinds <= {"unchanged"} and kinds else
               "old library: replaced" if "old" in kinds else "not checked")
        if name in curated: fix = "fixed (hand-made patch)"
        if manual: fix += f" ({manual} for the author)"
        status, note = ISSUES.get(name, (None, ""))
        mine = {s for s, p in SOURCES.items() if p == name}
        if not played_in: played = "not played yet"
        elif status: played = {"ok": "works", "minor": "works", "issue": "works, with a problem", "broken": "doesn't work",
                               "bench": "works"}[status]
        elif any(mine & errsrc[b] for b in played_in): played = "errors (not looked at yet)"
        else: played = "works, no errors"
        counts[played] += 1
        print(f"| {name} | {version} | {fix} | {played} | {note} |")
    print("\n" + ", ".join(f"{n} {k}" for k, n in sorted(counts.items(), key=lambda x: -x[1])) + ".")


main()
