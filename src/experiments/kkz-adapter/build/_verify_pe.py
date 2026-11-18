import json, sys
import pefile
path = sys.argv[1]
pe = pefile.PE(path, fast_load=False)
machine = pe.FILE_HEADER.Machine
exp = []
if hasattr(pe, "DIRECTORY_ENTRY_EXPORT") and pe.DIRECTORY_ENTRY_EXPORT:
    for s in pe.DIRECTORY_ENTRY_EXPORT.symbols:
        if s.name:
            exp.append(s.name.decode("ascii", "replace"))
want = ["V2Link", "V2Unlink", "GetModuleThreadModel"]
out = {
    "path": path,
    "machine": hex(machine),
    "is_x86": machine == 0x14C,
    "is_dll": bool(pe.FILE_HEADER.Characteristics & 0x2000),
    "exports": exp,
    "missing": [w for w in want if w not in exp],
    "extra": [e for e in exp if e not in want],
    "subsystem": pe.OPTIONAL_HEADER.Subsystem,
}
print(json.dumps(out, ensure_ascii=False, indent=2))
sys.exit(0 if (out["is_x86"] and out["is_dll"] and not out["missing"]) else 1)
