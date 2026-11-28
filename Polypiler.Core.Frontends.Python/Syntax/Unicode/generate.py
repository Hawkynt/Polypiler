import gzip, sys, os, re

ucd, out = sys.argv[1], sys.argv[2]

def ranges(path, prop):
    result = []
    for line in open(os.path.join(ucd, path), encoding="utf-8"):
        line = line.split("#")[0].strip()
        if not line:
            continue
        cps, name = [part.strip() for part in line.split(";")[:2]]
        if name != prop:
            continue
        lo, _, hi = cps.partition("..")
        result.append((int(lo, 16), int(hi or lo, 16)))
    result.sort()
    merged = []
    for lo, hi in result:
        if merged and merged[-1][1] + 1 == lo:
            merged[-1] = (merged[-1][0], hi)
        else:
            merged.append((lo, hi))
    return merged

def write(name, lines):
    data = ("\n".join(lines) + "\n").encode("utf-8")
    with open(os.path.join(out, name), "wb") as f:
        with gzip.GzipFile(fileobj=f, mode="wb", mtime=0, compresslevel=9, filename="") as g:
            g.write(data)

# Names: every explicit name of DerivedName.txt but the Hangul syllables (derived by rule NR1) and the
# patterns "PREFIX-*" (rule NR2, derived from the code point), then every alias of NameAliases.txt.
names = []
patterns = []
for line in open(os.path.join(ucd, "DerivedName.txt"), encoding="utf-8"):
    line = line.split("#")[0].strip()
    if not line:
        continue
    cps, name = [part.strip() for part in line.split(";")]
    if name.endswith("*"):
        lo, _, hi = cps.partition("..")
        patterns.append("P %s %s %s" % (lo, hi or lo, name[:-1]))
    elif name.startswith("HANGUL SYLLABLE "):
        continue
    else:
        names.append("%s %s" % (cps, name))
for line in open(os.path.join(ucd, "NameAliases.txt"), encoding="utf-8"):
    line = line.split("#")[0].strip()
    if not line:
        continue
    cp, alias, _kind = [part.strip() for part in line.split(";")]
    names.append("%s %s" % (cp, alias))
write("names.txt.gz", patterns + names)

# Properties: XID_Start and XID_Continue as ranges, the canonical combining class and the decomposition
# of every code point that has one, and the code points of Full_Composition_Exclusion.
props = []
for lo, hi in ranges("DerivedCoreProperties.txt", "XID_Start"):
    props.append("S %X %X" % (lo, hi))
for lo, hi in ranges("DerivedCoreProperties.txt", "XID_Continue"):
    props.append("C %X %X" % (lo, hi))
for lo, hi in ranges("DerivedNormalizationProps.txt", "Full_Composition_Exclusion"):
    props.append("X %X %X" % (lo, hi))
for line in open(os.path.join(ucd, "UnicodeData.txt"), encoding="utf-8"):
    fields = line.rstrip("\n").split(";")
    cp, ccc, decomposition = fields[0], int(fields[3]), fields[5]
    if not decomposition and ccc == 0:
        continue
    compat = decomposition.startswith("<")
    mapping = re.sub(r"^<[^>]*> ?", "", decomposition)
    props.append("D %s %d %s %s" % (cp, ccc, "K" if compat else "C", mapping.replace(" ", ",") or "-"))
write("properties.txt.gz", props)
print(len(names), len(patterns), len(props))
