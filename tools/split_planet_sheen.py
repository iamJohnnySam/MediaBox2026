import os, sys, shutil, re
SRC="/molecule/Media/TVShows/Jimmy Neutron (2002)"
DST="/molecule/Media/TVShows/Planet Sheen (2010)"
apply='--apply' in sys.argv
items=[f for f in sorted(os.listdir(SRC)) if f.upper().startswith('PLANET SHEEN')]
# "Ep. 34-E5" is a typo: two titles, and its neighbours are 32-33 and 36-37.
fix=lambda f: f.replace('Ep. 34-E5 -', 'Ep. 34-35 -')
print(f"{len(items)} entries -> {DST}")
for f in items:
    n=fix(f)
    print(f"   {f[:70]}" + (f"\n     renamed: {n[:68]}" if n!=f else ""))
if not apply: print("\ndry run"); raise SystemExit
os.makedirs(DST,exist_ok=True)
moved=0
for f in items:
    d=os.path.join(DST,fix(f))
    if os.path.exists(d): print("exists, skipped:",f[:60]); continue
    shutil.move(os.path.join(SRC,f), d); moved+=1
print(f"\nmoved {moved}")
