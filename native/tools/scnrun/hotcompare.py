# The "[hot]" lists of a run of the C# player's ScnBoot (SCNBOOT_HOT) and of scnrun compared:
# the instructions one ran that the other did not, and those run a different number of times. With
# --partial (scnrun stopped on an opcode it does not have yet: its "[hot so far]" list), only the
# instructions scnrun ran that ScnBoot did not, and those it ran more often.
#
#   python hotcompare.py <ScnBoot output> <scnrun output> [--partial]
import re
import sys


def read(path):
    hot = {}
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            m = re.match(r"\s*\[hot(?: so far)?\] slot (\d+) (\S+) ([0-9A-F]{4}) x(\d+)", line)
            if m:
                hot[(int(m[1]), m[2].lower(), m[3])] = int(m[4])
    return hot


def main():
    cs, cpp = read(sys.argv[1]), read(sys.argv[2])
    partial = "--partial" in sys.argv
    differ = 0
    for key in sorted(set(cs) | set(cpp), key=lambda k: (k[1], k[0])):
        a, b = cs.get(key), cpp.get(key)
        if b is None and partial:
            continue
        if a != b and not (partial and a is not None and b <= a):
            differ += 1
            if differ <= 40:
                print(f"  slot {key[0]} {key[1]} {key[2]}: C# {a}, C++ {b}")
    print(f"{len(cpp)} instructions of C++, {len(cs)} of C#: {differ} different")
    return 1 if differ else 0


sys.exit(main())
