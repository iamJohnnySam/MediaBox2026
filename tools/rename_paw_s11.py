import os, re, sys, shutil

SHOW = "/molecule/Media/TVShows/PAW Patrol (2013)"
SEASON = os.path.join(SHOW, "Season 11")

# Only the 11 files whose titles matched TVmaze with certainty (>=0.85) AND whose
# 23.2-min runtime confirms two segments. The other four name one segment only.
MAP = {1: (1, 2), 2: (3, 4), 3: (5, 6), 5: (9, 10), 8: (14, 15), 11: (19, 20),
       13: (23, 24), 16: (28, 29), 19: (33, 34), 24: (42, 43), 26: (45, 46)}
SKIP = {6, 14, 18, 21}

apply = '--apply' in sys.argv
plan = []
for f in sorted(os.listdir(SHOW)):
    m = re.match(r'(?i)^(PAW[. ]Patrol[. ])S11E(\d{2})(.+)$', f)
    if not m:
        continue
    pre, num, tail = m.group(1), int(m.group(2)), m.group(3)
    if num in SKIP or num not in MAP:
        continue
    a, b = MAP[num]
    # The titles in these names are already right — only the episode tag is wrong.
    plan.append((f, f"{pre}S11E{a:02d}E{b:02d}{tail}"))

print(f"{len(plan)} entries (mkv + thumb + trickplay), moving into 'Season 11/'\n")
for old, new in plan:
    print(f"  {old[:66]}\n-> {new[:66]}\n")

if not apply:
    print("dry run — pass --apply to perform")
    raise SystemExit

os.makedirs(SEASON, exist_ok=True)
for old, new in plan:
    src, dst = os.path.join(SHOW, old), os.path.join(SEASON, new)
    if os.path.exists(dst):
        print(f"SKIP, target exists: {new}")
        continue
    shutil.move(src, dst)
print(f"\nmoved {len(plan)} entries into {SEASON}")
