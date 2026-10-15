#!/usr/bin/env python3
"""
Lists episodes held more than once and, with --apply, deletes the copy you did not keep.

Dry run by default. Nothing is deleted without --apply.

An "episode held twice" means two video files parsing to the same show+season+episode.
Three things are deliberately NOT treated as duplicates:

  * segment files  - "SpongeBob S01E01a/b/c" are different 11-minute segments of one
                     numbered episode, not three copies of it.
  * paired files   - "S13E01E02" covers two episodes; it only collides with a single
                     that is genuinely the same content.
  * extras         - "S09X01" shorts, which the parser no longer numbers at all.

Choosing which copy to keep:

  KEEP-LARGEST is the default and is right for most rows, but NOT universally. In this
  library the We Bare Bears S04 1080p files are SMALLER than their 720p counterparts
  (x265 vs h264), so keep-largest throws away the 1080p copy. Rows where the two files
  differ in advertised resolution are therefore reported but SKIPPED unless you pass
  --include-mixed-resolution, and --prefer-resolution keeps the highest advertised
  resolution instead of the biggest file.

Usage:
    python3 prune_duplicate_episodes.py                      # report
    python3 prune_duplicate_episodes.py --apply              # delete, same-resolution rows only
    python3 prune_duplicate_episodes.py --prefer-resolution --include-mixed-resolution --apply
    python3 prune_duplicate_episodes.py --identical-only --apply   # only byte-identical copies
"""
import argparse, collections, hashlib, os, re, sys

TV_ROOT = "/molecule/Media/TVShows"
VIDEO = {'.mkv', '.mp4', '.avi', '.m4v', '.webm', '.flv', '.ts', '.mov', '.mpg', '.mpeg', '.wmv'}

# Mirrors FileNameParser: the E-prefixed and bare paired forms, plus "Ep. NN-NN".
TAG  = re.compile(r'(?i)S(\d{1,2})E(\d{1,3})(?:[-._+\s]*E(\d{1,3})|\s*[-&]\s*(\d{1,2})(?![\dA-Za-z,]))?')
EPONLY = re.compile(r'(?i)Ep\.?\s*(\d{1,3})(?:\s*[-–]\s*(\d{1,3})(?![\dA-Za-z,]))?')
SEG  = re.compile(r'(?i)S\d{1,2}E\d{1,3}[a-f](?![A-Za-z0-9])')
# Case-sensitive, as the parser is. Both forms appear: "S09X01" standalone, and "S07E17X01"
# hanging off the episode the extra belongs to.
XTRA = re.compile(r'S(\d{1,2})(?:E\d{1,3})?X(\d{1,3})(?![A-Za-z0-9])')
QUAL = re.compile(r'(?i)\b(\d{3,4})p\b')
MAX_SPAN = 10

# A corrected re-release is the one to keep even though it is usually smaller: a PROPER or
# REPACK exists because the first one was broken, and "__fixaudio" says so outright.
PREFERRED = re.compile(r'(?i)(\bPROPER\b|\bREPACK\b|\bFIXED\b|fixaudio|\bRERIP\b)')


def parse(name):
    """-> (season, first, last) or None."""
    if XTRA.search(name):
        return None
    m = TAG.search(name)
    if m:
        s, a = int(m.group(1)), int(m.group(2))
        end = m.group(3) or m.group(4)
        b = int(end) if end else a
        return (s, a, b if a < b <= a + MAX_SPAN else a)
    m = EPONLY.search(name)
    if m:
        a = int(m.group(1))
        b = int(m.group(2)) if m.group(2) else a
        return (1, a, b if a < b <= a + MAX_SPAN else a)
    return None


def title_tokens(name):
    """Words after the SxxExx tag and before the quality tag -- the episode title, if any."""
    base = os.path.splitext(name)[0]
    m = TAG.search(base)
    if not m:
        return set()
    rest = base[m.end():]
    q = QUAL.search(rest)
    if q:
        rest = rest[:q.start()]
    return {w.lower() for w in re.split(r'[^A-Za-z0-9]+', rest) if w}


def show_filler(names):
    """Words a show repeats on most of its files: release group, source, title formula."""
    freq = collections.Counter()
    for n in names:
        freq.update(title_tokens(n))
    limit = max(2, len(names) * 3 // 10)
    return {w for w, c in freq.items() if c > limit}


def resolution(name):
    m = QUAL.search(name)
    return int(m.group(1)) if m else None


def md5(path, limit=None):
    h = hashlib.md5()
    with open(path, 'rb') as fh:
        while chunk := fh.read(1 << 20):
            h.update(chunk)
    return h.hexdigest()


def sidecars(video):
    """Jellyfin's artwork and trickplay for one video, named off its stem."""
    stem, _ = os.path.splitext(video)
    out = []
    for suffix in ('-thumb.jpg', '.trickplay', '.srt', '.nfo'):
        p = stem + suffix
        if os.path.exists(p):
            out.append(p)
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--apply', action='store_true', help='actually delete')
    ap.add_argument('--prefer-resolution', action='store_true',
                    help='keep the highest advertised resolution instead of the largest file')
    ap.add_argument('--include-mixed-resolution', action='store_true',
                    help='also act on rows whose copies advertise different resolutions')
    ap.add_argument('--identical-only', action='store_true',
                    help='only act on byte-identical copies (md5 verified)')
    args = ap.parse_args()

    claims = collections.defaultdict(list)
    for dirpath, _, files in os.walk(TV_ROOT):
        show = os.path.relpath(dirpath, TV_ROOT).split(os.sep)[0]
        for f in files:
            if os.path.splitext(f)[1].lower() not in VIDEO:
                continue
            got = parse(f)
            if not got:
                continue
            s, a, b = got
            for ep in range(a, b + 1):
                claims[(show, s, ep)].append(os.path.join(dirpath, f))

    all_in_show = collections.defaultdict(set)
    for key, paths in claims.items():
        all_in_show[key[0]].update(paths)

    rows, skipped_mixed, collisions, freed = [], [], [], 0
    for key, paths in sorted(claims.items()):
        paths = sorted(set(paths))
        if len(paths) < 2:
            continue
        if sum(1 for p in paths if SEG.search(os.path.basename(p))) > 1:
            continue                                    # distinct segments, not copies

        # Same number but different episode titles is a numbering COLLISION, not a copy --
        # exactly what Planet Sheen and the We Bare Bears shorts were. Deleting one would
        # destroy an episode, so these are reported and never acted on.
        filler = show_filler([os.path.basename(p) for p in all_in_show[key[0]]])
        distinct = [(p, title_tokens(os.path.basename(p)) - filler) for p in paths]
        judgeable = [t for _, t in distinct if len(t) >= 2]
        if len(judgeable) >= 2:
            a, b = judgeable[0], judgeable[1]
            overlap = len(a & b) / len(a | b)
            if overlap < 0.5 and not (a <= b or b <= a):
                collisions.append((key, distinct))
                continue

        entries = [(os.path.getsize(p), resolution(os.path.basename(p)), p) for p in paths]
        res = [r for _, r, _ in entries if r]
        mixed = len(set(res)) > 1

        if args.prefer_resolution and res:
            entries.sort(key=lambda e: (e[1] or 0, e[0]), reverse=True)
        else:
            entries.sort(reverse=True)

        # A corrected re-release outranks size.
        corrected = [e for e in entries if PREFERRED.search(os.path.basename(e[2]))]
        if corrected and len(corrected) < len(entries):
            entries = corrected + [e for e in entries if e not in corrected]

        keep, drop = entries[0], entries[1:]
        identical = len({sz for sz, _, _ in entries}) == 1 and md5(keep[2]) == md5(drop[0][2])

        if args.identical_only and not identical:
            continue
        if mixed and not args.include_mixed_resolution:
            skipped_mixed.append((key, entries))
            continue

        note = ''
        if keep[1] is None and any(r for _, r, _ in drop):
            note = '  [CHECK: the copy being dropped advertises a resolution, the kept one does not]'
        rows.append((key, keep, drop, identical, note))
        freed += sum(sz for sz, _, _ in drop)

    for (show, s, ep), keep, drop, identical, note in rows:
        print(f"{show} S{s:02d}E{ep:02d}" + ("   [byte-identical]" if identical else "") + note)
        print(f"   keep  {keep[0]/2**20:7.0f} MB  {os.path.basename(keep[2])}")
        for sz, _, p in drop:
            print(f"   DROP  {sz/2**20:7.0f} MB  {os.path.basename(p)}")
            for sc in sidecars(p):
                print(f"         + sidecar      {os.path.basename(sc)}")

    if collisions:
        print(f"\nSKIPPED {len(collisions)} rows that are NOT duplicates -- two different episodes")
        print("sharing one number. Renumber or move one of each pair; never delete:")
        for (show, s_, ep), distinct in collisions:
            print(f"   {show} S{s_:02d}E{ep:02d}")
            for p, toks in distinct:
                print(f"      {os.path.basename(p)[:72]}")
                print(f"         names: {' '.join(sorted(toks)) or '(no title)'}")

    if skipped_mixed:
        print(f"\nSKIPPED {len(skipped_mixed)} rows whose copies advertise different resolutions.")
        print("Check these by hand -- a smaller file is often the better encode:")
        for (show, s, ep), entries in skipped_mixed:
            desc = " | ".join(f"{r or '?'}p {sz/2**20:.0f}MB" for sz, r, _ in entries)
            print(f"   {show} S{s:02d}E{ep:02d}: {desc}")
        print("   re-run with --include-mixed-resolution (and probably --prefer-resolution) to act on them")

    print(f"\n{len(rows)} rows actionable, {freed/2**30:.2f} GB would be freed")

    if not args.apply:
        print("\nDRY RUN -- nothing deleted. Add --apply to delete the files marked DROP.")
        return

    removed = 0
    for _, _, drop, _, _ in rows:
        for _, _, p in drop:
            for sc in sidecars(p):
                if os.path.isdir(sc):
                    import shutil; shutil.rmtree(sc)
                else:
                    os.remove(sc)
            os.remove(p)
            removed += 1
    print(f"\ndeleted {removed} video files and their sidecars, {freed/2**30:.2f} GB freed")
    print("run a MediaBox scan afterwards so the catalog and Jellyfin catch up:")
    print("  grpcurl -plaintext -import-path MediaBox2026/Protos -proto mediabox_control.proto \\")
    print("    -d '{}' localhost:5602 mediabox.control.MediaBoxControl/Scan")


if __name__ == '__main__':
    main()
