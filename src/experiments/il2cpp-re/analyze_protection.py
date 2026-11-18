# -*- coding: utf-8 -*-
"""IL2CPP 产物保护/加壳检测（只检测，不脱壳）。
用法: python analyze_protection.py <game_dir> [--json out.json]
检测项:
  1) 关键文件存在性/大小/SHA256
  2) PE 节区表 + 每节熵（熵 > 7.2 且节名可疑 -> 疑似压缩/加密）
  3) 已知壳节名/导入特征（UPX/ASPack/Themida/VMProtect/Enigma/MPRESS/.NET Reactor）
  4) global-metadata.dat 魔数/版本/头部合理性
  5) 元数据内 CJK 字面量可读性（加密/混淆时不可读）
"""
import hashlib
import json
import math
import os
import struct
import sys

import pefile

TARGETS = [
    "GameAssembly.dll",
    "UnityPlayer.dll",
    None,  # <exe>，运行时填
    r"SiliconHeart_Data\il2cpp_data\Metadata\global-metadata.dat",
    r"SiliconHeart_Data\baselib.dll",
    "baselib.dll",
]

SUSPECT_SECTIONS = {
    "UPX0": "UPX", "UPX1": "UPX", "UPX2": "UPX", ".upx": "UPX",
    ".aspack": "ASPack", ".adata": "ASPack",
    ".themida": "Themida/WinLicense", ".winlice": "Themida/WinLicense",
    ".vmp0": "VMProtect", ".vmp1": "VMProtect", ".vmp2": "VMProtect",
    ".enigma1": "Enigma Protector", ".enigma2": "Enigma Protector",
    ".mpress1": "MPRESS", ".mpress2": "MPRESS",
    ".perplex": "Perplex", "PEC2": "PECompact", ".petite": "Petite",
    ".netshrink": "NETShrink", "BitArts": "Crunch",
}


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest().upper()


def entropy(data):
    if not data:
        return 0.0
    counts = [0] * 256
    for b in data:
        counts[b] += 1
    n = len(data)
    e = 0.0
    for c in counts:
        if c:
            p = c / n
            e -= p * math.log2(p)
    return round(e, 3)


def pe_report(path):
    out = {"path": path, "size": os.path.getsize(path), "sha256": sha256(path)}
    pe = pefile.PE(path, fast_load=True)
    pe.parse_data_directories(directories=[
        pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_IMPORT"],
        pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_RESOURCE"],
    ])
    out["machine"] = hex(pe.FILE_HEADER.Machine)
    out["timestamp"] = pe.FILE_HEADER.TimeDateStamp
    out["image_base"] = hex(pe.OPTIONAL_HEADER.ImageBase)
    out["subsystem"] = pe.OPTIONAL_HEADER.Subsystem
    out["dllcharacteristics"] = hex(pe.OPTIONAL_HEADER.DllCharacteristics)
    out["overlay_size"] = getattr(pe, "get_overlay_data_start_offset", lambda: None)() and (
        out["size"] - pe.get_overlay_data_start_offset()) or 0
    secs = []
    total_entropy_bytes = 0
    for s in pe.sections:
        name = s.Name.rstrip(b"\x00").decode("latin-1", "replace")
        data = s.get_data()
        e = entropy(data)
        secs.append({
            "name": name,
            "vaddr": hex(s.VirtualAddress),
            "vsize": s.Misc_VirtualSize,
            "rawsize": s.SizeOfRawData,
            "entropy": e,
            "suspicious": SUSPECT_SECTIONS.get(name, ""),
        })
        total_entropy_bytes += len(data)
    out["sections"] = secs
    out["section_count"] = len(secs)
    imps = []
    for entry in getattr(pe, "DIRECTORY_ENTRY_IMPORT", []) or []:
        imps.append(entry.dll.decode("latin-1", "replace"))
    out["import_dlls"] = sorted(imps)
    pe.close()
    return out


def metadata_report(path):
    out = {"path": path, "size": os.path.getsize(path), "sha256": sha256(path)}
    with open(path, "rb") as f:
        head = f.read(0x120)
        f.seek(0)
        blob = f.read()
    magic, version = struct.unpack_from("<Ii", head, 0)
    out["magic"] = hex(magic & 0xFFFFFFFF)
    out["magic_ok"] = (magic & 0xFFFFFFFF) == 0xFAB11BAF
    out["metadata_version"] = version
    # 头部字段（Il2CppGlobalMetadataHeader）
    names = ["sanity", "version", "stringLiteralOffset", "stringLiteralCount",
             "stringLiteralDataOffset", "stringLiteralDataCount",
             "stringOffset", "stringCount", "eventsOffset", "eventsCount",
             "propertiesOffset", "propertiesCount", "methodsOffset", "methodsCount",
             "parameterDefaultValuesOffset", "parameterDefaultValuesCount",
             "fieldDefaultValuesOffset", "fieldDefaultValuesCount",
             "fieldAndParameterDefaultValueDataOffset", "fieldAndParameterDefaultValueDataCount",
             "fieldMarshaledSizesOffset", "fieldMarshaledSizesCount",
             "parametersOffset", "parametersCount", "fieldsOffset", "fieldsCount",
             "genericParametersOffset", "genericParametersCount",
             "genericParameterConstraintsOffset", "genericParameterConstraintsCount",
             "genericContainersOffset", "genericContainersCount",
             "nestedTypesOffset", "nestedTypesCount", "interfacesOffset", "interfacesCount",
             "vtableMethodsOffset", "vtableMethodsCount",
             "interfaceOffsetsOffset", "interfaceOffsetsCount",
             "typeDefinitionsOffset", "typeDefinitionsCount",
             "imagesOffset", "imagesCount", "assembliesOffset", "assembliesCount",
             "fieldRefsOffset", "fieldRefsCount", "referencedAssembliesOffset", "referencedAssembliesCount",
             "attributeDataOffset", "attributeDataCount",
             "attributeDataRangeOffset", "attributeDataRangeCount",
             "unresolvedIndirectCallParameterTypesOffset", "unresolvedIndirectCallParameterTypesCount",
             "unresolvedIndirectCallParameterRangesOffset", "unresolvedIndirectCallParameterRangesCount",
             "windowsRuntimeTypeNamesOffset", "windowsRuntimeTypeNamesSize",
             "windowsRuntimeStringsOffset", "windowsRuntimeStringsSize",
             "exportedTypeDefinitionsOffset", "exportedTypeDefinitionsCount"]
    fields = {}
    for i, nm in enumerate(names):
        if (i + 1) * 4 <= len(head):
            fields[nm] = struct.unpack_from("<i", head, i * 4)[0]
    out["header_fields"] = fields
    # 头部合理性：偏移+长度 应落在文件内
    bad = []
    for i in range(2, len(names), 2):
        off = fields.get(names[i])
        cnt = fields.get(names[i + 1])
        if off is None or cnt is None or off < 0 or cnt < 0:
            continue
        if off == 0 and cnt == 0:
            continue
        limit = max(off + cnt, off + cnt * 1)  # 字节数（Count 为字节数）
        if limit > out["size"]:
            bad.append({"field": names[i], "off": off, "len": cnt, "end": limit})
    out["header_bounds_violations"] = bad
    # CJK 字面量可读性
    text = blob.decode("utf-8", "ignore")
    cjk = sum(1 for ch in text if "\u4e00" <= ch <= "\u9fff")
    out["cjk_chars_in_utf8_decode"] = cjk
    out["utf8_decode_ok"] = cjk > 50
    return out


def main():
    if len(sys.argv) < 2:
        print("usage: analyze_protection.py <game_dir> [--json out]")
        return 2
    g = sys.argv[1]
    out_json = None
    if "--json" in sys.argv:
        out_json = sys.argv[sys.argv.index("--json") + 1]
    exe = None
    for f in os.listdir(g):
        if f.lower().endswith(".exe") and "crashhandler" not in f.lower():
            exe = f
            break
    result = {"game_dir": g, "exe": exe, "files": {}, "notes": []}
    for t in TARGETS:
        rel = exe if t is None else t
        if not rel:
            continue
        p = os.path.join(g, rel)
        if not os.path.exists(p):
            result["files"][rel] = {"missing": True}
            continue
        try:
            if rel.lower().endswith(".dat"):
                result["files"][rel] = metadata_report(p)
            elif rel.lower().endswith(".dll") or rel.lower().endswith(".exe"):
                result["files"][rel] = pe_report(p)
        except Exception as e:  # noqa: BLE001
            result["files"][rel] = {"error": "%s: %s" % (type(e).__name__, e)}
    # 壳判定
    suspects = []
    for name, rep in result["files"].items():
        for s in rep.get("sections", []) or []:
            if s.get("suspicious"):
                suspects.append({"file": name, "section": s["name"], "packer": s["suspicious"]})
    result["packer_suspects"] = suspects
    result["verdict"] = {
        "packed": bool(suspects),
        "metadata_plaintext": result["files"].get(
            r"SiliconHeart_Data\il2cpp_data\Metadata\global-metadata.dat", {}).get("magic_ok", False),
    }
    txt = json.dumps(result, ensure_ascii=False, indent=2)
    if out_json:
        os.makedirs(os.path.dirname(out_json), exist_ok=True)
        with open(out_json, "w", encoding="utf-8") as f:
            f.write(txt)
        print("written " + out_json)
    else:
        print(txt)
    return 0


if __name__ == "__main__":
    sys.exit(main())

