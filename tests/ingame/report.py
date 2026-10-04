"""Pilot runs -> one self-contained HTML page (<out-dir>/index.html, screenshots inlined) to look through and share.

    python report.py <out-dir> <label>=<pilot-dir> [<label>=<pilot-dir> ...]

A pilot dir is <profile>/run/pilot (match) or .../pilot-host and .../pilot-join (online); host/join pairs get a desync table.
NOTES says what is known about an error source; anything not in it shows as "not looked at yet".
"""
import base64, html, io, os, re, shutil, sys
from PIL import Image

NOTES = {
    "PlayerAIZorro": ("game", "The game's own AI finding no hiding spots on the map. Only the test's AI players run it; "
                              "the test now caps the search."),
    "ClassesManagerReborn": ("mod", "Classes Manager Reborn's own bug: after JACK is picked with no class mods installed, "
                                    "every pick-end hook indexes an empty list. Not caused by the port."),
    "BepInEx": ("bench", "Two copies of the game sharing one profile's config files. Test setup only; fixed by giving "
                         "the second copy its own profile."),
    "CodeAnimation": ("game", "The game's own card animations starting on hidden objects. Unity logs it with no stack; "
                              "the test traced the caller."),
    "FriendlyFoe": ("game", "The game's 2025 bullet pool: releasing a bullet whose pool is already gone throws inside the "
                            "pool's own error handler. Bullets still get cleaned up."),
    "ProjectileHit": ("game", "The game's bullet code starting a coroutine on a bullet that was already put away."),
    "FROZE": ("bench", "The game's own AI looping forever looking for a hiding spot on a map that has none far enough "
                       "away (PlayerAIZorro.GetPosAwayFrom has no retry limit). Only the test's AI players run it; the test "
                       "now caps the search."),
    "Pilot": ("bench", "The test itself (for example the joining copy losing its connection to Photon)."),
    "CR": ("mod", "Cosmic Rounds' own bugs, on the old game too. Drive measures the distance to the nearest living "
                  "enemy without checking there is one, so it throws in the moment after a kill. Drone threw on every "
                  "bullet every frame; the Runtime now stops that. Flex reads its player every 0.1 s but only learns who "
                  "that is the first time the player deals damage, so it throws until then."),
    "CR (patched)": ("mod", "Cosmic Rounds' Careen hooks the bullet's bounce handler when the bullet starts, but the game "
                            "only gives bullets that handler when they bounce (old game too), so it throws on its last line "
                            "for a bullet with no bounces. Everything it sets up before that works. (patched): the "
                            "Runtime's check that skips Cosmic Rounds' templates."),
    "UnboundLib": ("mod", "UnboundLib fades in a text label for 4 seconds without checking it still exists. The test "
                          "leaves the menu sooner than a player would."),
    "DamageOverTime": ("game", "The game applying damage over time to a player who just died."),
    "Block": ("game", "The game running a block for a player who just died."),
    "MapTransition (patched)": ("game", "Debris from a box broken just before the round ends gets destroyed while the "
                                        "map slides out, and the slide step for that piece throws. The map change itself "
                                        "finishes. The old game does the same; Performance Improvements only shows in "
                                        "the trace because it rewrites this method."),
    "ModdingUtils": ("game", "A bullet hit names the map collider it hit by its index, counted on the shooter's machine. "
                             "Box debris and broken pieces leave the list at different moments on each machine, so the "
                             "index can miss and ModdingUtils' auto-block check throws. The game's own hit code does the "
                             "same lookup, on the old game too. Health stays in sync: damage is sent separately. "
                             "Locally it shows when the thing hit was destroyed in the same moment (box debris)."),
    "ProjectileHit (patched)": ("game", "A hit can reach the other machine in the same packet as the bullet itself, "
                                        "before the bullet has started there, and the game's hit code throws. Same on "
                                        "the old game. Damage is sent separately, so health stays in sync; the push on a "
                                        "map box and on-hit effects are lost on that machine."),
    "NetworkPhysicsObject (patched)": ("game", "The same early hit, pushing a map box."),
    "PlayerManager": ("game", "Both players died in the same frame (an explosion): the game mode asks for the last "
                              "player alive and there is none. Same on the old game; the next point starts normally."),
    "StickFightMaps": ("mod", "Stick Fight Maps was built for MapsExtended 0.9.5 and calls a method MapsExtended 1.4.2 no longer has, so its maps load without their objects and the round stalls. Same on the old game with this MapsExtended."),
    "WWC": (None, "Will's Wacky Cards' Momentum card looks for its card-face text when the card starts; on the 2025 card "
                  "the text isn't there yet, so its changing description doesn't update. Cosmetic."),
    "PCE": (None, "PCE's Random card effect looks for its card-face text when the card starts; on the 2025 card it isn't "
                  "there yet, so the effect throws every frame while the card is on screen. Cosmetic."),
    "Arcana": (None, "Arcana's Crimson Aura finds a collider on the player layer with no player above it and stops, so "
                     "players after it in the list aren't hit by the aura. Seen in one match, not in the reruns."),
    "CrimsonAura": (None, "Arcana's Crimson Aura finds a collider on the player layer with no player above it and stops, "
                          "so players after it in the list aren't hit by the aura. Seen in one match, not in the reruns."),
    "ItemShops": ("bench", "Item Shops reads every player's controls each frame; the test's AI players have none."),
    "Supcom2Cards": ("port", "Picking Darkenoid threw: it looks for its player with GetComponentInParent while the picker is "
                             "dead (inactive), and Unity 2022 finds nothing there, so the pick never finished. The Runtime "
                             "now looks again including inactive objects."),
    "StunPlayer": ("game", "The game's own stun effect on a player who is gone. Also with no mods installed."),
    "ParticleExplosionModifier": ("game", "The game's own explosion particles starting without their owner. Also with no mods installed."),
    "LineEffect": ("game", "The game's own line effects starting on hidden objects. Also with no mods installed."),
    "Sonigon": ("game", "The game's sound engine playing a sound for an object that is gone. Also with no mods installed."),
    "Explosion": ("game", "The game's explosions damaging an object that is already gone."),
    "MapTransition": ("game", "Debris from a box broken just before the round ends gets destroyed while the map slides "
                              "out, and the slide step for that piece throws. The map change finishes. Same on the old game."),
    "Unity: asset layout": ("mod", "Unity logs this once at startup: a mod's asset bundle (Simulation Chamber) was built "
                                   "against the old game's version of a script. Nothing was found broken by it."),
    "Unity: particles": ("mod", "Unity 2022 checks that a particle effect's orbital velocity curves all use one mode; "
                                "some MFM card effects mix them. Logged once per card; the cards work."),
    "Unity: UI masks": ("bench", "Unity's UI allows 127 nested masks. It started after 66 picks in the ZomC-only run, far "
                                 "more cards than a real match gives."),
    "Photon: view IDs": ("bench", "Photon gives each player 999 network IDs, and in a local match every player shares one "
                                  "set. Near the end of a 43-pick match (bullets plus ZomC's Double Vision spawning a network "
                                  "object per shot) they ran out for a few shots. Online each player has their own set."),
    "Infoholic": (None, "Infoholic reads the local player's gun once when a match starts, before there is a player. "
                        "Once a match."),
    "HermitHandler": (None, "Arcana's Hermit starts a coroutine on its aura at battle start while the aura object is "
                            "inactive; Unity skips that coroutine. Not traced further."),
    "Explosion_Overpower": ("game", "The game's Overpower explosion hitting a player that is already gone."),
    "BounceTrigger": ("game", "The game's bounce cards hook the bullet's bounce handler when the bullet starts; a bullet "
                              "that ends up with no bounces has no handler, and the game's own Start throws."),
    "ScreenEdgeBounce": ("game", "The game's screen-edge bounce for a bullet that was already removed."),
    "RemoteControl": ("bench", "The game's Remote card steers with the player's controls; the test's AI players have none."),
    "DamagableEvent": ("game", "The game's explosions damaging a map object that is already gone."),
    "DamageBox": ("game", "The game's damage boxes hitting something that is already gone."),
    "ZomC_Cards": ("mod", "ZomC's Double Vision effect runs Start on its bullet template, which has no bullet (once at "
                          "startup, once per pick). Perseverance (Pristine) used to throw too (the picker is dead, so a "
                          "ModdingUtils effect's Awake hadn't run); the Runtime now fills it in. That error shows only in "
                          "runs from before the fix."),
    "WillsWackyManagers": ("bench", "Will's Wacky Managers sets its Table Flip card's rarity when a room is joined. The test "
                                    "joins a room seconds after the menu appears, before that card is ready. Once, at the "
                                    "start."),
    "RootCore": (None, "Root Core folds its card categories under one \"Root\" entry in the Toggle Cards menu by finding "
                       "parts of UnboundLib 3's menu; UnboundLib 4's menu is built differently, so the folding stops and "
                       "each Root category shows as its own row. Menu only; the toggles work."),
    "TMPro": (None, "Lobby Improvements (below): it reads its lobby text before the text is set up."),
    "LobbyImprovements": (None, "Lobby Improvements sorts its lobby list a frame after a change and reads a text "
                                "object that isn't set up when a local match starts. Once a match; nothing visible."),
    "WeaponsManager": ("bench", "Weapons Manager reads every player's controls each frame; the test's AI players have "
                                "none. Real players always do."),
    "PickPhaseImprovements": ("port", "The 2025 game clears the picker the moment a card is picked; Pick Phase "
                                      "Improvements reads it to deal the next hand and the match stayed in the pick phase. "
                                      "The Runtime puts it back for that step."),
    "Photon": ("game", "Photon removing an object the other machine had already removed."),
    "Steam": ("bench", "Both copies run on one Steam account, so each sees the other's lobby."),
}

# the summary at the top: (status, text)
FINDINGS = [
    ("Fixed", "Online, a modded card one player picked was a dead reference on the other player's game, so anything "
              "that checks a player's cards (ModdingUtils' card rules, Cosmic Rounds' Beetle) behaved differently on "
              "each machine. Cause: UnboundLib makes each modded card its own source card, and the 2025 game only looks "
              "the source up when it is empty. Runtime 1.3.0 clears it. Desync check: every snapshot differed before, "
              "none after."),
    ("Fixed", "Cosmic Rounds' Drone threw an error for every bullet on every frame. The Runtime skips the two "
              "springs nothing assigns."),
    ("Fixed", "With Pick Phase Improvements or Pick N Cards, the match stayed in the pick phase after the first card: "
              "the 2025 game clears the picker as soon as a card is picked, and they read it to deal the next hand. "
              "The Runtime puts it back for that step."),
    ("Fixed", "Fancy Card Bar and Will's Wacky Cards read the card bar's card by its old field name and threw on "
              "every pick. AutoFix now renames it in UnboundLib's reflection helpers too."),
    ("Fixed", "Picking Supcom2's Darkenoid or ZomC's Perseverance (Pristine) threw and the pick phase never finished. "
              "The picker is the round's loser, dead (inactive) during the pick: Darkenoid's player lookup finds nothing "
              "on an inactive object, and Perseverance uses a ModdingUtils effect whose Awake hasn't run. The Runtime "
              "covers both, only where the original would throw. Not checked against the old build."),
    ("New", "One Thunderstore package for players on any mod manager: AutoFix swaps the old UnboundLib, MMHook and "
            "RoundsWithFriends for Bknibb's ports, applies the hand-made patches and fixes the rest; the Runtime and "
            "Odin Serializer come with it. Tested on a profile laid out exactly as r2modman does it."),
    ("Explained", "Bullet hits that sometimes fail on the other machine online are the game's own: a hit can arrive "
                  "before the bullet has started there, and map collider indexes drift as debris is removed. Same on "
                  "the old game; health stays in sync."),
    ("Explained", "In the online package test (Cosmic Rounds + Classes Manager Reborn), 4 of 117 snapshots differ, all in "
                  "gun damage only: Cosmic Rounds' Hive, Charge and Enchant change damage for a moment around a block, "
                  "and each copy takes its snapshot at a slightly different moment. Cards and health match."),
    ("Explained", "Restore test on the package profile: RestoreOriginals put back every original file exactly, and "
                  "turning AutoFix back on fixed the same 15 files again."),
    ("Test only", "The game's AI can freeze the game on maps with no hiding spot far enough away; the test now caps "
                  "its search. The lobby staying on screen during battles came from the test skipping the intro screen."),
]

# lines with no stack, grouped by who logs them
SHOTS = 14   # screenshots per run on the page

LOGGED_BY = [("MissingMethodException: Method not found: void MapsExt.MapsExtended.OnPhotonMapObjectInstantiate", "StickFightMaps"),
             ("A scripted object (script unknown or not yet loaded) has a different serialization layout", "Unity: asset layout"),
             ("Particle Orbital Velocity curves must all be in the same mode", "Unity: particles"),
             ("Maximum number of mask per levels has been exceeded", "Unity: UI masks"),
             ("Exception: AllocateViewID() failed", "Photon: view IDs"), ("Ev Destroy Failed", "Photon"), ("Getting Lobbychat update", "Steam"), ("Receive issue", "Pilot"),
             ("Connection lost", "Pilot"), ("Network restart", "Pilot")]


def rows(path):
    if not os.path.exists(path): return []
    return [l.rstrip("\r\n").split("\t") for l in open(path, encoding="utf-8", newline="\n")][1:]


def esc(s): return html.escape(s, quote=True)


def shots(src, dst, prefix):
    """PNG screenshots -> 960px JPEG data URIs, so the page is one file; [(uri, kind, caption)]"""
    out = []
    d = os.path.join(src, "shots")
    if not os.path.isdir(d): return out
    # ponytail: a fixed sample per run keeps a 12-run page under the 16 MB limit; all error shots first
    files = sorted(f for f in os.listdir(d) if f.endswith(".png"))
    errs = [f for f in files if "_error" in f][:6]
    rest = [f for f in files if f not in errs]
    keep = errs + rest[::max(1, len(rest) // max(1, SHOTS - len(errs)))][:SHOTS - len(errs)]
    for f in sorted(keep):
        im = Image.open(os.path.join(d, f)).convert("RGB")
        im.thumbnail((960, 540))
        buf = io.BytesIO()
        im.save(buf, "JPEG", quality=72)
        name = "data:image/jpeg;base64," + base64.b64encode(buf.getvalue()).decode()
        m = re.match(r"\d+_([a-z]+)_?(.*)", f[:-4])
        kind, rest = (m.group(1), m.group(2)) if m else ("other", f)
        cap = rest.replace("__CR__", "").replace("__CMR__", "").replace("-", " ").strip() or kind
        out.append((name, kind, cap))
    return out


def main():
    dst, runs = sys.argv[1], [a.split("=", 1) for a in sys.argv[2:]]
    shutil.rmtree(dst, ignore_errors=True)
    os.makedirs(dst)
    sections, gallery, errors = [], [], {}
    for label, d in runs:
        cards = rows(os.path.join(d, "cards.tsv"))
        picked = [r for r in cards if len(r) > 1 and not r[1].startswith("(not offered")]
        skipped = [r for r in cards if len(r) > 1 and r[1].startswith("(not offered")]
        for src, line in (r for r in rows(os.path.join(d, "errors.tsv")) if len(r) == 2):
            line = re.sub(r"\(at /[^)]*\)", "", line)   # build paths a patched DLL's .pdb carried
            src = next((s for p, s in LOGGED_BY if line.startswith(p)), src)
            errors.setdefault(src, []).append((label, line))
        g = shots(d, dst, re.sub(r"\W+", "-", label.lower()))
        gallery += [(label, *s) for s in g]
        sections.append((label, picked, skipped, len(g)))

    # desync: host/join pairs
    desync = []
    by = dict(runs)
    for label, d in runs:
        if not label.endswith("host"): continue
        other = by.get(label[:-4] + "join")
        if not other: continue
        a = {(r[0], r[1]): r[2:] for r in rows(os.path.join(d, "state.tsv")) if len(r) == 5}
        b = {(r[0], r[1]): r[2:] for r in rows(os.path.join(other, "state.tsv")) if len(r) == 5}
        both = sorted(set(a) & set(b), key=lambda k: (int(k[0]), k[1]))
        desync.append((label[:-4].strip(), both, a, b))

    p = []
    w = p.append
    for label, picked, skipped, nshots in sections:
        mods = sorted({r[1] for r in picked})
        w(f'<div class="run"><h3>{esc(label)}</h3><dl>'
          f'<div><dt>Cards picked</dt><dd>{len(picked)}</dd></div>'
          f'<div><dt>Not offered</dt><dd>{len(skipped)}</dd></div>'
          f'<div><dt>Screenshots</dt><dd>{nshots}</dd></div></dl>'
          f'<p class="mods">From {esc(", ".join(mods)) or "no cards"}</p></div>')
    runs_html = "".join(p)

    p = []
    order = sorted(errors, key=lambda s: ({"port": 0, None: 1, "mod": 2, "game": 3, "bench": 4}[NOTES.get(s, (None,))[0]], s))
    for src in order:
        kind, note = NOTES.get(src, (None, "Not looked at yet."))
        tag = {"game": "Game", "mod": "Mod's own bug", "bench": "Test setup", "port": "Port", None: "Open"}[kind]
        lines = "".join(f'<li><span class="where">{esc(l)}</span><code>{esc(e.split(" | ")[0])}</code>'
                        f'<span class="ctx">{esc(" | ".join(e.split(" | ")[1:]))}</span></li>' for l, e in errors[src][:6])
        more = f'<li class="more">and {len(errors[src]) - 6} more</li>' if len(errors[src]) > 6 else ""
        w(f'<details class="err" data-kind="{kind or "open"}"><summary><span class="tag">{tag}</span>'
          f'<b>{esc(src)}</b><span class="n">{len(errors[src])}</span></summary><p>{esc(note)}</p><ul>{lines}{more}</ul></details>')
    errors_html = "".join(p) or '<p class="ok">No errors.</p>'

    p = []
    for label, both, a, b in desync:
        bad = [k for k in both if a[k] != b[k]]
        w(f'<p class="verdict {"ok" if not bad else "bad"}">{len(both)} player snapshots seen by both copies; '
          f'{len(bad)} disagree.</p>')
        if both:
            w('<div class="scroll"><table><thead><tr><th>Battle</th><th>Player</th><th>Cards</th><th>Max health</th>'
              '<th>Damage</th><th>Same on both</th></tr></thead><tbody>')
            for k in both:
                same = a[k] == b[k]
                cell = lambda i: esc(a[k][i]) if a[k][i] == b[k][i] else f'{esc(a[k][i])}<br><s>{esc(b[k][i])}</s>'
                cards = cell(0).replace("__CR__", "").replace("__CMR__", "").replace(",", ", ")
                w(f'<tr class="{"" if same else "diff"}"><td>{k[0]}</td><td>{k[1]}</td><td>{cards or "none"}</td>'
                  f'<td>{cell(1)}</td><td>{cell(2)}</td><td>{"yes" if same else "no"}</td></tr>')
            w('</tbody></table></div>')
    desync_html = "".join(p) or '<p class="muted">No online run in this report.</p>'

    kinds = sorted({k for _, _, k, _ in gallery})
    filters = "".join(f'<button type="button" data-k="{k}">{k}</button>' for k in kinds)
    figs = "".join(f'<figure data-k="{k}"><button type="button" class="shot"><img src="{f}" loading="lazy" '
                   f'alt="{esc(label)}: {esc(c)}"></button><figcaption><span>{esc(label)}</span>{esc(c)}</figcaption></figure>'
                   for label, f, k, c in gallery)

    page = open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "report-template.html"), encoding="utf-8").read()
    findings_html = "".join(f'<li><span class="tag" data-s="{esc(s)}">{esc(s)}</span><p>{esc(t)}</p></li>' for s, t in FINDINGS)
    for key, val in {"FINDINGS": findings_html, "RUNS": runs_html, "ERRORS": errors_html, "DESYNC": desync_html, "FILTERS": filters, "GALLERY": figs}.items():
        page = page.replace("{{" + key + "}}", val)
    open(os.path.join(dst, "index.html"), "w", encoding="utf-8").write(page)
    print(f"{dst}\\index.html: {len(gallery)} screenshots, {len(errors)} error sources")


main()
