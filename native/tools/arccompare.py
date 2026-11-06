# Compares arcdump's lines with the files ScnTools extract wrote for the same game: every file the
# C# player decodes must come out of the C++ archives with the same bytes, and no other.
#   python arccompare.py <arcdump.txt> <ScnTools extract folder>
import hashlib
import os
import sys

def main():
    sys.stdout.reconfigure(encoding="utf-8")
    dump, folder = sys.argv[1], sys.argv[2]
    native = {}
    errors = []
    with open(dump, encoding="utf-8") as f:
        for line in f:
            line = line.rstrip("\n")
            if not line or line.startswith("#"):
                print(line)
                continue
            name, size, flags, hash_ = line.split("\t")
            if hash_.startswith("ERROR"):
                errors.append((name, hash_))
            native[name.casefold()] = (name, hash_)

    expected = {}
    for name in os.listdir(folder):
        with open(os.path.join(folder, name), "rb") as f:
            expected[name.casefold()] = (name, hashlib.sha256(f.read()).hexdigest().upper())

    differ = [n for k, (n, h) in expected.items() if k in native and native[k][1] != h]
    missing = [n for k, (n, _) in expected.items() if k not in native]
    extra = [n for k, (n, _) in native.items() if k not in expected]
    print(f"C# files {len(expected)}, C++ files {len(native)}: same {len(expected) - len(differ) - len(missing)}, "
          f"differ {len(differ)}, only C# {len(missing)}, only C++ {len(extra)}, C++ errors {len(errors)}")
    for title, names in (("differ", differ), ("only C#", missing), ("only C++", extra)):
        for n in names[:20]:
            print(f"  {title}: {n}")
    for n, e in errors[:20]:
        print(f"  error: {n} {e}")
    return 0 if not (differ or missing or extra or errors) else 1

if __name__ == "__main__":
    sys.exit(main())
