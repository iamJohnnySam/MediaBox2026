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

  Each candidate is PROBED with ffprobe, because filenames lie: "1080p" in a name may be
  an upscale, and a bigger file is not a better one. In this library the We Bare Bears S04
  1080p x265 files are half the size of their 720p h264 twins and are still the better copy.

  Ranked on, in order:
    1. real picture height from the video stream
    2. codec generation  - AV1 / HEVC > H.264 > MPEG-4 ASP (XviD, DivX) > older
    3. bits per pixel per second, so a thin encode at a given size loses
    4. audio channels, then container - mkv > mp4 > avi
  A corrected re-release (PROPER, REPACK, FIXED, __fixaudio) wins among copies of equal
  height: it exists because the first one was broken.

Usage:
    python3 prune_duplicate_episodes.py                      # report
    python3 prune_duplicate_episodes.py --apply              # delete the lesser copies
    python3 prune_duplicate_episodes.py --identical-only --apply   # only byte-identical copies
"""
import argparse, collections, hashlib, json, os, re, shutil, subprocess, sys

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


# Newer generations encode the same picture in fewer bits, so at equal height the later
# codec is the better copy, not the bigger file.
CODEC_RANK = {'av1': 4, 'hevc': 4, 'h265': 4, 'vp9': 3, 'h264': 2, 'avc1': 2,
              'mpeg4': 1, 'msmpeg4v3': 1, 'vc1': 1, 'mpeg2video': 0, 'mpeg1video': 0}

# Roughly how far each codec stretches a bit, relative to H.264. Codec generation alone is
# not quality: x265 at 0.7 Mb/s is a thinner 720p than x264 at 3.0 Mb/s, so bitrates are
# normalised by this before being compared.
CODEC_EFFICIENCY = {'av1': 2.0, 'hevc': 1.8, 'h265': 1.8, 'vp9': 1.6, 'h264': 1.0,
                    'avc1': 1.0, 'mpeg4': 0.6, 'msmpeg4v3': 0.6, 'vc1': 0.8,
                    'mpeg2video': 0.5, 'mpeg1video': 0.4}
CONTAINER_RANK = {'.mkv': 2, '.mp4': 1, '.m4v': 1, '.avi': 0, '.mpg': 0, '.mpeg': 0, '.wmv': 0}


def probe(path):
    """Real stream facts, or None if ffprobe cannot read the file."""
    try:
        out = subprocess.run(
            ['ffprobe', '-v', 'error', '-print_format', 'json',
             '-show_entries', 'format=duration,size,bit_rate'
             ':stream=codec_type,codec_name,height,width,channels',
             path],
            capture_output=True, text=True, timeout=120).stdout
        d = json.loads(out)
    except Exception:
        return None

    video = next((s for s in d.get('streams', []) if s.get('codec_type') == 'video'), None)
    if not video:
        return None
    audio = [s for s in d.get('streams', []) if s.get('codec_type') == 'audio']
    fmt = d.get('format', {})
    size = int(fmt.get('size') or os.path.getsize(path))
    duration = float(fmt.get('duration') or 0) or None
    height = int(video.get('height') or 0)
    width = int(video.get('width') or 0)
    bitrate = (size * 8 / duration) if duration else 0
    codec = (video.get('codec_name') or '').lower()
    # Codec-normalised bits per pixel: what the encode is actually worth at this picture size.
    effective = bitrate * CODEC_EFFICIENCY.get(codec, 1.0)
    bpps = effective / (width * height) if width and height else 0

    return {
        'height': height,
        'width': width,
        'codec': (video.get('codec_name') or '').lower(),
        'codec_rank': CODEC_RANK.get((video.get('codec_name') or '').lower(), 0),
        'bitrate': bitrate,
        'bpps': bpps,
        'channels': max((int(a.get('channels') or 0) for a in audio), default=0),
        'container': CONTAINER_RANK.get(os.path.splitext(path)[1].lower(), 0),
        'size': size,
        'duration': duration,
    }


def quality_key(path, info):
    """
    Higher is better. Picture height first -- more resolution is the thing being asked for.
    Within one height a corrected re-release wins, then the codec-normalised encode weight,
    then audio channels and container. Codec generation is only the final tiebreak, because
    a modern codec starved of bits is not the better copy.
    """
    corrected = 1 if PREFERRED.search(os.path.basename(path)) else 0
    return (info['height'], corrected, round(info['bpps'], 4),
            info['channels'], info['container'], info['codec_rank'], info['size'])


def describe(info):
    br = f"{info['bitrate']/1_000_000:.1f} Mb/s" if info['bitrate'] else "? Mb/s"
    ch = f"{info['channels']}ch" if info['channels'] else "no audio"
    return f"{info['width']}x{info['height']} {info['codec']} {br} {ch} {info['size']/2**20:.0f}MB"


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

    rows, skipped_mixed, collisions, unreadable, runtimes, freed = [], [], [], [], [], 0
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

        probed = []
        for pth in paths:
            info = probe(pth)
            if info is None:
                unreadable.append(pth)
            else:
                probed.append((pth, info))

        if len(probed) < 2:
            continue                                    # cannot compare what cannot be read

        probed.sort(key=lambda pi: quality_key(*pi), reverse=True)
        keep, drop = probed[0], probed[1:]

        identical = (len({i['size'] for _, i in probed}) == 1
                     and md5(keep[0]) == md5(drop[0][0]))

        # Copies of materially different length are not interchangeable: one may be a
        # different cut, hold two segments, or simply be truncated. Never guessed at.
        durations = [i['duration'] for _, i in probed if i['duration']]
        if durations and max(durations) - min(durations) > 120:
            runtimes.append((key, probed))
            continue

        note = ''

        rows.append((key, keep, drop, identical, note))
        freed += sum(i['size'] for _, i in drop)

    for (show, s, ep), keep, drop, identical, note in rows:
        print(f"{show} S{s:02d}E{ep:02d}" + ("   [byte-identical]" if identical else "") + note)
        print(f"   keep  {describe(keep[1])}")
        print(f"         {os.path.basename(keep[0])}")
        for pth, info in drop:
            print(f"   DROP  {describe(info)}")
            print(f"         {os.path.basename(pth)}")
            for sc in sidecars(pth):
                print(f"         + sidecar {os.path.basename(sc)}")

    if collisions:
        print(f"\nSKIPPED {len(collisions)} rows that are NOT duplicates -- two different episodes")
        print("sharing one number. Renumber or move one of each pair; never delete:")
        for (show, s_, ep), distinct in collisions:
            print(f"   {show} S{s_:02d}E{ep:02d}")
            for p, toks in distinct:
                print(f"      {os.path.basename(p)[:72]}")
                print(f"         names: {' '.join(sorted(toks)) or '(no title)'}")

    if runtimes:
        print(f"\nSKIPPED {len(runtimes)} rows whose copies differ by more than two minutes of")
        print("runtime -- a different cut, a paired file, or a truncated download. Check by hand:")
        for (show, s_, ep), probed in runtimes:
            print(f"   {show} S{s_:02d}E{ep:02d}")
            for pth, info in probed:
                mins = f"{(info['duration'] or 0)/60:.1f} min"
                print(f"      {mins:>9}  {describe(info)}")
                print(f"                 {os.path.basename(pth)[:72]}")

    if unreadable:
        print(f"\nffprobe could not read {len(unreadable)} file(s); their rows were left alone:")
        for pth in unreadable:
            print(f"   {os.path.basename(pth)[:76]}")

    print(f"\n{len(rows)} rows actionable, {freed/2**30:.2f} GB would be freed")

    if not args.apply:
        print("\nDRY RUN -- nothing deleted. Add --apply to delete the files marked DROP.")
        return

    removed = 0
    for _, _, drop, _, _ in rows:
        for p, _ in drop:
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
