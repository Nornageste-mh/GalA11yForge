import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Data;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.FunctionIterator;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.listing.InstructionIterator;
import ghidra.program.model.listing.Listing;
import ghidra.program.model.mem.Memory;
import ghidra.program.model.mem.MemoryBlock;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;
import ghidra.program.model.symbol.ReferenceManager;
import ghidra.program.model.symbol.Symbol;
import ghidra.program.model.symbol.SymbolIterator;
import ghidra.program.model.symbol.SymbolTable;

import java.io.File;
import java.io.OutputStreamWriter;
import java.io.Writer;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.security.MessageDigest;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.TreeMap;

/**
 * CandidateHookDump.java - Gal 无障碍辅助 Demo 快速生成工具（GalA11y Demo） Ghidra backend (schema gala11y.ghidra.candidates/1).
 *
 * Locates Win32 GDI / USER32 / DirectWrite / Direct2D text output APIs in a native PE,
 * walks import slot -> thunk -> caller to find candidate functions, extracts evidence
 * (CJK string references, caller counts, name hints) and writes a machine-readable
 * candidate list with a 0-100 confidence score. Read-only w.r.t. the analysed binary.
 *
 * Usage inside analyzeHeadless:
 *   -postScript CandidateHookDump.java <outputDir>
 *
 * Output: <outputDir>/candidates.json
 */
public class CandidateHookDump extends GhidraScript {

    private static final String SCHEMA = "gala11y.ghidra.candidates/1";

    /** Direct text-output APIs with base score, category and hook strategy family. */
    private static final Map<String, ApiSpec> TEXT_APIS = new LinkedHashMap<>();
    static {
        TEXT_APIS.put("GetGlyphOutlineW", new ApiSpec("GetGlyphOutlineW", "GDI32.dll", 38, "glyph"));
        TEXT_APIS.put("TextOutW", new ApiSpec("TextOutW", "GDI32.dll", 35, "gdi"));
        TEXT_APIS.put("ExtTextOutW", new ApiSpec("ExtTextOutW", "GDI32.dll", 35, "gdi"));
        TEXT_APIS.put("DrawTextW", new ApiSpec("DrawTextW", "USER32.dll", 33, "gdi"));
        TEXT_APIS.put("DrawTextExW", new ApiSpec("DrawTextExW", "USER32.dll", 33, "gdi"));
        TEXT_APIS.put("GetTextExtentPoint32W", new ApiSpec("GetTextExtentPoint32W", "GDI32.dll", 18, "measure"));
        TEXT_APIS.put("SetWindowTextW", new ApiSpec("SetWindowTextW", "USER32.dll", 20, "window-title"));
        TEXT_APIS.put("DWriteCreateFactory", new ApiSpec("DWriteCreateFactory", "DWrite.dll", 30, "directwrite"));
        TEXT_APIS.put("D2D1CreateFactory", new ApiSpec("D2D1CreateFactory", "D2D1.dll", 30, "direct2d"));
    }

    private static final Set<String> DYNAMIC_RESOLVERS = new LinkedHashSet<>(java.util.Arrays.asList(
            "GetProcAddress", "LoadLibraryW", "LoadLibraryA", "LoadLibraryExW", "LdrGetProcedureAddress"));

    private static final class ApiSpec {
        final String name;
        final String dll;
        final int baseScore;
        final String category;

        ApiSpec(String name, String dll, int baseScore, String category) {
            this.name = name;
            this.dll = dll;
            this.baseScore = baseScore;
            this.category = category;
        }
    }

    private static final class StringHit {
        final Address address;
        final String text;
        final int cjk;
        final int xrefs;
        final List<String> functions = new ArrayList<>();

        StringHit(Address address, String text, int cjk, int xrefs) {
            this.address = address;
            this.text = text;
            this.cjk = cjk;
            this.xrefs = xrefs;
        }
    }

    private static final class Candidate {
        Function function;
        final Map<String, String> apiIat = new LinkedHashMap<>();
        final Set<String> apis = new LinkedHashSet<>();
        int score;
        int callerCount;
        final List<String> callers = new ArrayList<>();
        final List<StringHit> stringRefs = new ArrayList<>();
        int cjkStringRefs;
        boolean nameHint;
        boolean debugHint;
        String level;
        String strategy;
    }

    private final StringBuilder sb = new StringBuilder(1 << 16);
    private final List<String[]> hints = new ArrayList<>();
    private final Map<Long, StringHit> cjkByAddress = new LinkedHashMap<>();
    private long scriptStart;
    private int definedStrings;
    private int definedCjkStrings;
    private int strictCjkStrings;
    private int totalCjkStrings;
    private int scanCandidates;
    private long instructionsWalked;

    @Override
    public void run() throws Exception {
        scriptStart = System.currentTimeMillis();
        String[] args = getScriptArgs();
        String outDir = args.length > 0 ? args[0]
                : new File(currentProgram.getExecutablePath()).getParent() + File.separator + "ghidra-out";
        File out = new File(outDir);
        if (!out.exists() && !out.mkdirs()) {
            printerr("Cannot create output dir: " + outDir);
            return;
        }

        Memory memory = currentProgram.getMemory();
        Listing listing = currentProgram.getListing();
        ReferenceManager rm = currentProgram.getReferenceManager();
        SymbolTable st = currentProgram.getSymbolTable();

        String exePath = currentProgram.getExecutablePath();
        if (exePath != null && exePath.startsWith("/") && exePath.length() > 2
                && exePath.charAt(2) == ':') {
            exePath = exePath.substring(1);
        }
        String sha256 = sha256Of(exePath);
        boolean isDotnet = false;
        boolean dynamicResolution = false;
        List<String> dlls = new ArrayList<>();
        List<String> present = new ArrayList<>();
        Map<String, List<Address>> apiSlots = new LinkedHashMap<>();

        // ---- imports ---------------------------------------------------------
        SymbolIterator ext = st.getExternalSymbols();
        while (ext.hasNext()) {
            Symbol s = ext.next();
            String n = s.getName();
            if (n == null) {
                continue;
            }
            String parent = s.getParentNamespace() != null ? s.getParentNamespace().getName() : "";
            if (!dlls.contains(parent)) {
                dlls.add(parent);
            }
            if ("mscoree.dll".equalsIgnoreCase(parent) || "_CorExeMain".equalsIgnoreCase(n)) {
                isDotnet = true;
            }
            if (DYNAMIC_RESOLVERS.contains(n)) {
                dynamicResolution = true;
            }
            ApiSpec spec = findSpec(n);
            if (spec == null) {
                continue;
            }
            if (!present.contains(spec.name)) {
                present.add(spec.name);
            }
            List<Address> slots = apiSlots.get(spec.name);
            if (slots == null) {
                slots = new ArrayList<>();
                apiSlots.put(spec.name, slots);
            }
            ReferenceIterator refs = rm.getReferencesTo(s.getAddress());
            while (refs.hasNext()) {
                Address from = refs.next().getFromAddress();
                if (!slots.contains(from)) {
                    slots.add(from);
                }
            }
        }
        List<String> missing = new ArrayList<>();
        for (ApiSpec spec : TEXT_APIS.values()) {
            if (!present.contains(spec.name)) {
                missing.add(spec.name);
            }
        }
        if (!isDotnet) {
            isDotnet = looksDotnet(memory);
        }

        // ---- packing heuristic (section entropy), needed before the string scan --
        Map<String, Double> entropies = new LinkedHashMap<>();
        boolean packedSuspected = false;
        for (MemoryBlock b : memory.getBlocks()) {
            double h = blockEntropy(memory, b);
            entropies.put(b.getStart().toString(), h);
            if (b.isExecute() && b.getSize() >= 65536 && h >= 7.2) {
                packedSuspected = true;
            }
        }

        // ---- CJK string anchors: defined strings (+ strict scan if not packed) --
        List<StringHit> cjkStrings = collectCjkStrings(listing, memory, rm, !packedSuspected);

        // ---- candidates: import slot -> thunk -> callers ----------------------
        Map<Long, Candidate> byEntry = new TreeMap<>();
        for (Map.Entry<String, List<Address>> e : apiSlots.entrySet()) {
            ApiSpec spec = findSpec(e.getKey());
            if (spec == null) {
                continue;
            }
            for (Address slot : e.getValue()) {
                for (Function f : callersThroughImport(slot, rm)) {
                    if (f == null || f.isExternal() || f.isThunk()) {
                        continue;
                    }
                    long key = f.getEntryPoint().getUnsignedOffset();
                    Candidate c = byEntry.get(key);
                    if (c == null) {
                        c = new Candidate();
                        c.function = f;
                        byEntry.put(key, c);
                    }
                    c.apis.add(spec.name);
                    c.apiIat.put(spec.name, slot.toString());
                }
            }
        }
        // Fallback: some loaders emit no references to the IAT slot. Walk every
        // instruction and match its operand target against slot / external addresses.
        if (byEntry.isEmpty()) {
            collectCandidatesByGlobalScan(apiSlots, byEntry, listing);
        }
        for (Candidate c : byEntry.values()) {
            scoreCandidate(c, listing, rm);
        }

        List<Candidate> ranked = new ArrayList<>(byEntry.values());
        ranked.sort(Comparator.comparingInt((Candidate c) -> c.score).reversed()
                .thenComparing(c -> c.function.getEntryPoint().getUnsignedOffset()));

        // ---- engine hints ----------------------------------------------------
        if (packedSuspected) {
            hints.add(new String[] { "packed-suspected",
                    "可执行段熵≥7.2 且体积≥64KB：疑似加壳/加密，导入表虽可读，但调用点位于压缩代码中，"
                    + "静态候选为空属正常。降级：先脱壳，或用运行期追踪（ETW/调试器/Textractor 对照）。" });
        }
        if (isDotnet) {
            hints.add(new String[] { "dotnet",
                    "PE 含 mscoree/_CorExeMain：托管程序集，Ghidra 只看到 native stub，"
                    + "文本 API 需走 .NET 分析（ilspycmd/Cecil）；本报告不作为 hook 依据。" });
        }
        if (present.isEmpty()) {
            hints.add(new String[] { "no-static-text-api",
                    "静态导入表未命中任何文本 API；可能为运行时解析/托管/自绘（SDL、DirectX 自带字形）。"
                    + "降级：suggest-other-route。" });
        }
        if (!present.contains("DWriteCreateFactory") && !present.contains("D2D1CreateFactory")) {
            hints.add(new String[] { "no-directwrite",
                    "未导入 DWrite/D2D：排除 DirectWrite 文本栈，优先 GDI 路径判断。" });
        }
        if (dynamicResolution) {
            hints.add(new String[] { "dynamic-resolution",
                    "导入 GetProcAddress/LoadLibrary：文本 API 可能运行时解析，静态候选只覆盖已导入部分。" });
        }
        if (present.contains("GetGlyphOutlineW")) {
            hints.add(new String[] { "glyph-outline",
                    "命中 GetGlyphOutlineW：KiriKiri/KAG 系常见字体轮廓路径，"
                    + "Textractor 同点位（texthook/engine/engine.cc:1388-1445）可对照。" });
        }

        // ---- JSON ------------------------------------------------------------
        sb.setLength(0);
        sb.append("{\n");
        field("schema", SCHEMA, true, 1);
        field("generated_at", java.time.OffsetDateTime.now().toString(), true, 1);
        obj("tool", 1);
        field("name", "Ghidra headless", true, 2);
        field("version", ghidra.framework.Application.getApplicationVersion(), true, 2);
        field("script", "CandidateHookDump.java", false, 2);
        close(true, 1);

        obj("binary", 1);
        field("path", exePath, true, 2);
        field("name", currentProgram.getName(), true, 2);
        field("sha256", sha256, true, 2);
        field("size_bytes", new File(exePath).length(), true, 2);
        field("executable_format", currentProgram.getExecutableFormat(), true, 2);
        field("language", currentProgram.getLanguageID().toString(), true, 2);
        field("compiler", currentProgram.getCompilerSpec().getCompilerSpecID().toString(), true, 2);
        field("bits", currentProgram.getDefaultPointerSize() * 8, true, 2);
        field("image_base", currentProgram.getImageBase().toString(), true, 2);
        Address entry = null;
        ghidra.program.model.address.AddressIterator eit = st.getExternalEntryPointIterator();
        if (eit.hasNext()) {
            entry = eit.next();
        }
        field("entry_point", entry == null ? "" : entry.toString(), true, 2);
        field("is_dotnet", isDotnet, true, 2);
        field("dynamic_api_resolution_suspected", dynamicResolution, true, 2);
        field("packed_suspected", packedSuspected, true, 2);
        arrayStart("sections", 2);
        int sc = 0;
        for (MemoryBlock b : memory.getBlocks()) {
            if (sc++ > 0) {
                sb.append(",\n");
            }
            sb.append("      {\"name\":").append(q(b.getName()))
              .append(",\"start\":").append(q(b.getStart().toString()))
              .append(",\"size\":").append(b.getSize())
              .append(",\"execute\":").append(b.isExecute())
              .append(",\"write\":").append(b.isWrite())
              .append(",\"entropy\":").append(String.format(Locale.ROOT, "%.2f",
                      entropies.getOrDefault(b.getStart().toString(), 0.0))).append("}");
        }
        sb.append("\n    ],\n");
        strArray("dlls", dlls, 2, false);
        close(true, 1);

        obj("text_api_surface", 1);
        strArray("present", present, 2, true);
        strArray("missing", missing, 2, true);
        arrayStart("import_slots", 2);
        int is = 0;
        for (Map.Entry<String, List<Address>> e : apiSlots.entrySet()) {
            ApiSpec spec = findSpec(e.getKey());
            if (spec == null) {
                continue;
            }
            if (is++ > 0) {
                sb.append(",\n");
            }
            sb.append("      {\"api\":").append(q(spec.name))
              .append(",\"dll\":").append(q(spec.dll))
              .append(",\"category\":").append(q(spec.category))
              .append(",\"slot\":").append(q(e.getValue().isEmpty() ? "" : e.getValue().get(0).toString()))
              .append(",\"ref_sites\":").append(e.getValue().size()).append("}");
        }
        sb.append("\n    ]\n");
        close(true, 1);

        arrayStart("candidates", 1);
        int limit = Math.min(ranked.size(), 40);
        for (int i = 0; i < limit; i++) {
            Candidate c = ranked.get(i);
            if (i > 0) {
                sb.append("\n");
            }
            sb.append("    {\n");
            field("rank", i + 1, true, 3);
            field("function", c.function.getName(true), true, 3);
            field("address", c.function.getEntryPoint().toString(), true, 3);
            field("confidence", c.score, true, 3);
            field("level", c.level, true, 3);
            strArray("apis", new ArrayList<>(c.apis), 3, true);
            obj("evidence", 3);
            field("caller_count", c.callerCount, true, 4);
            strArray("callers", c.callers, 4, true);
            field("cjk_string_refs", c.cjkStringRefs, true, 4);
            field("name_hint", c.nameHint, true, 4);
            field("debug_hint", c.debugHint, true, 4);
            arrayStart("strings", 4);
            int n = Math.min(c.stringRefs.size(), 8);
            for (int k = 0; k < n; k++) {
                if (k > 0) {
                    sb.append(",\n");
                }
                StringHit h = c.stringRefs.get(k);
                sb.append("        {\"address\":").append(q(h.address.toString()))
                  .append(",\"text\":").append(q(h.text))
                  .append(",\"cjk_chars\":").append(h.cjk).append("}");
            }
            sb.append("\n      ]\n");
            close(true, 3);
            obj("hook", 3);
            field("slot", c.apiIat.isEmpty() ? "" : c.apiIat.values().iterator().next(), true, 4);
            field("strategy", c.strategy, false, 4);
            close(false, 3);
            close(i < limit - 1, 2);
        }
        sb.append("\n  ],\n");

        List<StringHit> anchors = new ArrayList<>();
        for (StringHit h : cjkStrings) {
            if (h.xrefs > 0) {
                anchors.add(h);
            }
        }
        anchors.sort(Comparator.comparingInt((StringHit h) -> h.xrefs).reversed()
                .thenComparingInt(h -> -h.cjk));
        obj("string_table", 1);
        field("source", "ghidra_defined_strings", true, 2);
        field("defined_strings_total", definedStrings, true, 2);
        field("defined_cjk_strings", definedCjkStrings, true, 2);
        field("utf16_scan_cjk_strings", strictCjkStrings, true, 2);
        field("with_code_xref", anchors.size(), true, 2);
        arrayStart("top_anchors", 2);
        int an = Math.min(anchors.size(), 15);
        for (int i = 0; i < an; i++) {
            if (i > 0) {
                sb.append(",\n");
            }
            StringHit h = anchors.get(i);
            sb.append("      {\"address\":").append(q(h.address.toString()))
              .append(",\"text\":").append(q(h.text))
              .append(",\"cjk_chars\":").append(h.cjk)
              .append(",\"xrefs\":").append(h.xrefs)
              .append(",\"functions\":[");
            for (int k = 0; k < h.functions.size() && k < 6; k++) {
                if (k > 0) {
                    sb.append(", ");
                }
                sb.append(q(h.functions.get(k)));
            }
            sb.append("]}");
        }
        sb.append("\n    ]\n");
        close(true, 1);

        arrayStart("engine_hints", 1);
        for (int i = 0; i < hints.size(); i++) {
            if (i > 0) {
                sb.append(",\n");
            }
            sb.append("    {\"id\":").append(q(hints.get(i)[0]))
              .append(",\"text\":").append(q(hints.get(i)[1])).append("}");
        }
        sb.append("\n  ],\n");

        obj("stats", 1);
        field("functions", currentProgram.getFunctionManager().getFunctionCount(), true, 2);
        field("instructions_in_candidate_bodies", instructionsWalked, true, 2);
        field("defined_strings", definedStrings, true, 2);
        field("defined_cjk_strings", definedCjkStrings, true, 2);
        field("utf16_scan_cjk_strings", strictCjkStrings, true, 2);
        field("cjk_strings", totalCjkStrings, true, 2);
        field("global_scan_candidates", scanCandidates, true, 2);
        field("candidate_count", ranked.size(), true, 2);
        field("script_ms", System.currentTimeMillis() - scriptStart, false, 2);
        close(false, 1);
        sb.append("}\n");

        File jsonFile = new File(out, "candidates.json");
        try (Writer w = new OutputStreamWriter(Files.newOutputStream(jsonFile.toPath()),
                StandardCharsets.UTF_8)) {
            w.write(sb.toString());
        }
        println("WROTE " + jsonFile.getAbsolutePath() + " bytes=" + jsonFile.length()
                + " candidates=" + ranked.size() + " cjk_strings=" + totalCjkStrings
                + " packed=" + packedSuspected);
    }

    // ------------------------------------------------------------------ helpers

    private ApiSpec findSpec(String name) {
        for (ApiSpec s : TEXT_APIS.values()) {
            if (s.name.equalsIgnoreCase(name)) {
                return s;
            }
        }
        return null;
    }

    private List<Function> callersThroughImport(Address slot, ReferenceManager rm) {
        List<Function> result = new ArrayList<>();
        collectCallers(slot, rm, result, 0);
        return result;
    }

    private void collectCallers(Address target, ReferenceManager rm, List<Function> out, int depth) {
        if (depth > 3) {
            return;
        }
        ReferenceIterator refs = rm.getReferencesTo(target);
        while (refs.hasNext()) {
            Function f = currentProgram.getFunctionManager()
                    .getFunctionContaining(refs.next().getFromAddress());
            if (f == null) {
                continue;
            }
            if (f.isThunk()) {
                collectCallers(f.getEntryPoint(), rm, out, depth + 1);
            } else if (!f.isExternal() && !out.contains(f)) {
                out.add(f);
            }
        }
    }

    /**
     * Fallback discovery: match instruction operand references against IAT slots and
     * the external symbols themselves. Needed for binaries where no reference to the
     * slot is emitted (packed stubs, unusual loaders).
     */
    private void collectCandidatesByGlobalScan(Map<String, List<Address>> apiSlots,
            Map<Long, Candidate> byEntry, Listing listing) {
        Map<Long, String> targets = new LinkedHashMap<>();
        for (Map.Entry<String, List<Address>> e : apiSlots.entrySet()) {
            for (Address a : e.getValue()) {
                targets.put(a.getUnsignedOffset(), e.getKey());
            }
        }
        SymbolIterator syms = currentProgram.getSymbolTable().getExternalSymbols();
        while (syms.hasNext()) {
            Symbol s = syms.next();
            ApiSpec spec = findSpec(s.getName());
            if (spec != null) {
                targets.put(s.getAddress().getUnsignedOffset(), spec.name);
            }
        }
        if (targets.isEmpty()) {
            return;
        }
        InstructionIterator iit = listing.getInstructions(true);
        while (iit.hasNext()) {
            Instruction ins = iit.next();
            Reference[] refs = ins.getReferencesFrom();
            if (refs.length == 0) {
                continue;
            }
            Function f = currentProgram.getFunctionManager()
                    .getFunctionContaining(ins.getAddress());
            if (f == null || f.isExternal() || f.isThunk()) {
                continue;
            }
            for (Reference r : refs) {
                String api = targets.get(r.getToAddress().getUnsignedOffset());
                if (api == null) {
                    continue;
                }
                long key = f.getEntryPoint().getUnsignedOffset();
                Candidate c = byEntry.get(key);
                if (c == null) {
                    c = new Candidate();
                    c.function = f;
                    byEntry.put(key, c);
                    scanCandidates++;
                }
                c.apis.add(api);
                c.apiIat.put(api, r.getToAddress().toString());
            }
        }
    }

    private static double blockEntropy(Memory memory, MemoryBlock b) {
        if (!b.isInitialized() || b.getSize() < 16) {
            return 0.0;
        }
        int n = (int) Math.min(b.getSize(), 16L * 1024 * 1024);
        byte[] buf = new byte[n];
        try {
            memory.getBytes(b.getStart(), buf);
        } catch (Exception e) {
            return 0.0;
        }
        int[] freq = new int[256];
        for (byte x : buf) {
            freq[x & 0xff]++;
        }
        double h = 0.0;
        for (int f : freq) {
            if (f == 0) {
                continue;
            }
            double p = (double) f / n;
            h -= p * (Math.log(p) / Math.log(2));
        }
        return h;
    }

    private void scoreCandidate(Candidate c, Listing listing, ReferenceManager rm) {
        int best = 0;
        int extra = 0;
        String bestApi = null;
        for (String api : c.apis) {
            ApiSpec s = findSpec(api);
            if (s == null) {
                continue;
            }
            if (s.baseScore > best) {
                best = s.baseScore;
                bestApi = api;
            } else {
                extra += 5;
            }
        }
        extra = Math.min(extra, 10);

        Set<Function> callers = new LinkedHashSet<>();
        ReferenceIterator refs = rm.getReferencesTo(c.function.getEntryPoint());
        while (refs.hasNext()) {
            Function f = currentProgram.getFunctionManager()
                    .getFunctionContaining(refs.next().getFromAddress());
            if (f != null && !f.equals(c.function)) {
                callers.add(f);
            }
        }
        c.callerCount = callers.size();
        for (Function f : callers) {
            if (c.callers.size() < 5) {
                c.callers.add(f.getName(true));
            }
        }

        InstructionIterator ii = listing.getInstructions(c.function.getBody(), true);
        while (ii.hasNext()) {
            Instruction ins = ii.next();
            instructionsWalked++;
            for (Reference r : ins.getReferencesFrom()) {
                if (!r.getReferenceType().isData()) {
                    continue;
                }
                StringHit hit = cjkByAddress.get(r.getToAddress().getUnsignedOffset());
                if (hit == null) {
                    continue;
                }
                boolean dup = false;
                for (StringHit existing : c.stringRefs) {
                    if (existing.address.equals(hit.address)) {
                        dup = true;
                        break;
                    }
                }
                if (!dup) {
                    c.stringRefs.add(hit);
                }
            }
        }
        c.cjkStringRefs = c.stringRefs.size();

        String lower = c.function.getName(true).toLowerCase(Locale.ROOT);
        c.nameHint = lower.matches(".*(text|draw|message|msg|dialog|talk|line|print|glyph|render).*");
        c.debugHint = lower.matches(".*(debug|log|trace|assert|error|stats|fps|test).*");

        int score = best + extra;
        if (c.cjkStringRefs > 0) {
            score += 12;
        }
        if (c.callerCount >= 2) {
            score += 8;
        }
        if (c.nameHint) {
            score += 6;
        }
        if (c.debugHint) {
            score -= 15;
        }
        c.score = Math.max(0, Math.min(100, score));
        c.level = c.score >= 70 ? "high" : c.score >= 45 ? "medium" : "low";
        c.strategy = strategyFor(bestApi, c);
    }

    private String strategyFor(String api, Candidate c) {
        String slot = api == null ? "" : c.apiIat.getOrDefault(api, "");
        if ("GetGlyphOutlineW".equals(api)) {
            return "IAT hook (slot " + slot + ") + caller filter：在返回前抓 wchar_t 文本；"
                    + "KiriKiri/KAG 系已验证路径，需按调用者地址过滤系统绘制噪声。";
        }
        if ((api != null && api.startsWith("TextOut")) || "DrawTextW".equals(api)
                || "DrawTextExW".equals(api)) {
            return "IAT hook (slot " + slot + ") on " + api + "：直接取 lpString/nCount；"
                    + "注意 GDI 也画 UI 控件，需按调用者+文本长度过滤。";
        }
        if ("SetWindowTextW".equals(api)) {
            return "IAT hook (slot " + slot + ") on SetWindowTextW：只适合标题/状态栏，不能作为台词主通道。";
        }
        if (api != null && (api.contains("DWrite") || api.contains("D2D"))) {
            return "已导入 DirectWrite/D2D 工厂（slot " + slot + "），但文本提交在 IDWriteTextLayout::Draw，"
                    + "静态只能定位创建点，需运行期跟踪 layout 对象。";
        }
        if ("GetTextExtentPoint32W".equals(api)) {
            return "仅测量 API（slot " + slot + "）：可作为辅助断点，不直接携带台词文本。";
        }
        return "候选（slot " + slot + "）：建议运行期确认后再生成 hook。";
    }

    /**
     * Uses the strings Ghidra's analyzers actually defined (PascalUnicode, Unicode,
     * ASCII, ...). This is far more precise than raw UTF-16 scanning: on a packed
     * KiriKiri exe the raw scan returned ~27k garbage runs while the defined table
     * held 5.3k strings, 339 of them real Japanese.
     */
    private List<StringHit> collectDefinedCjkStrings(Listing listing, ReferenceManager rm) {
        List<StringHit> hits = new ArrayList<>();
        java.util.Iterator<Data> it = listing.getDefinedData(true);
        while (it.hasNext()) {
            Data d = it.next();
            Object value;
            try {
                value = d.getValue();
            } catch (Exception e) {
                continue;
            }
            if (!(value instanceof String)) {
                continue;
            }
            String text = (String) value;
            if (text.isEmpty() || text.length() > 2000) {
                continue;
            }
            definedStrings++;
            int cjk = countCjk(text);
            if (cjk < 3) {
                continue;
            }
            totalCjkStrings++;
            definedCjkStrings++;
            Address a = d.getAddress();
            int xrefs = 0;
            List<String> funcs = new ArrayList<>();
            ReferenceIterator ri = rm.getReferencesTo(a);
            while (ri.hasNext()) {
                xrefs++;
                Function f = currentProgram.getFunctionManager()
                        .getFunctionContaining(ri.next().getFromAddress());
                if (f != null && funcs.size() < 4 && !funcs.contains(f.getName(true))) {
                    funcs.add(f.getName(true));
                }
            }
            StringHit h = new StringHit(a, truncate(text, 80), cjk, xrefs);
            h.functions.addAll(funcs);
            hits.add(h);
            if (!cjkByAddress.containsKey(a.getUnsignedOffset())) {
                cjkByAddress.put(a.getUnsignedOffset(), h);
            }
        }
        return hits;
    }

    /** Defined strings always; strict UTF-16 scan only when the image is not packed. */
    private List<StringHit> collectCjkStrings(Listing listing, Memory memory,
            ReferenceManager rm, boolean allowStrictScan) {
        List<StringHit> hits = collectDefinedCjkStrings(listing, rm);
        if (allowStrictScan) {
            scanUtf16CjkStrict(memory, rm, hits);
        }
        List<StringHit> merged = new ArrayList<>();
        Set<Long> seen = new LinkedHashSet<>();
        for (StringHit h : hits) {
            if (seen.add(h.address.getUnsignedOffset())) {
                merged.add(h);
            }
        }
        return merged;
    }

    /**
     * Conservative raw scan for wide literals that Ghidra did not define (default
     * analysis only runs the ASCII Strings analyzer). Requirements: even alignment,
     * explicit NUL terminator, ASCII/CJK-only code units, and >=4 CJK characters.
     * This avoids the ~27k misaligned false positives seen on a packed KiriKiri exe.
     */
    private void scanUtf16CjkStrict(Memory memory, ReferenceManager rm, List<StringHit> hits) {
        for (MemoryBlock b : memory.getBlocks()) {
            if (!b.isInitialized() || b.isExecute() || b.getSize() > 64L * 1024 * 1024) {
                continue;
            }
            byte[] buf;
            try {
                buf = new byte[(int) b.getSize()];
                memory.getBytes(b.getStart(), buf);
            } catch (Exception e) {
                continue;
            }
            int i = 0;
            while (i + 8 <= buf.length) {
                int[] run = decodeStrictRun(buf, i);
                if (run[0] == 1) {
                    int chars = run[3];
                    String text = new String(buf, i, chars * 2, StandardCharsets.UTF_16LE);
                    Address a = b.getStart().add(i);
                    int xrefs = 0;
                    List<String> funcs = new ArrayList<>();
                    ReferenceIterator ri = rm.getReferencesTo(a);
                    while (ri.hasNext()) {
                        xrefs++;
                        Function f = currentProgram.getFunctionManager()
                                .getFunctionContaining(ri.next().getFromAddress());
                        if (f != null && funcs.size() < 4 && !funcs.contains(f.getName(true))) {
                            funcs.add(f.getName(true));
                        }
                    }
                    StringHit h = new StringHit(a, truncate(text, 80), run[2], xrefs);
                    h.functions.addAll(funcs);
                    hits.add(h);
                    totalCjkStrings++;
                    strictCjkStrings++;
                    if (!cjkByAddress.containsKey(a.getUnsignedOffset())) {
                        cjkByAddress.put(a.getUnsignedOffset(), h);
                    }
                    i += Math.max(2, chars * 2 + 2);
                } else {
                    i += 2;
                }
            }
        }
    }

    /** {valid, terminatorIndex, cjkCount, charCount} for an aligned strict UTF-16 run. */
    private int[] decodeStrictRun(byte[] buf, int i) {
        int cjk = 0;
        int kana = 0;
        int ascii = 0;
        int chars = 0;
        int p = i;
        int max = Math.min(buf.length - 1, i + 800);
        boolean terminated = false;
        while (p + 1 < max) {
            int u = (buf[p] & 0xff) | ((buf[p + 1] & 0xff) << 8);
            if (u == 0) {
                terminated = true;
                break;
            }
            if (isCjk(u)) {
                cjk++;
                if (u >= 0x3040 && u <= 0x30ff) {
                    kana++;
                }
            } else if (u >= 0x20 && u <= 0x7e) {
                ascii++;
            } else if (u < 0x20 || u == 0x7f
                    || (u > 0x7f && u < 0x3000 && !isTextPunct(u))
                    || (u >= 0x3000 && !isCjk(u) && !(u >= 0x3000 && u <= 0x303f)
                        && !(u >= 0xff00 && u <= 0xffef))) {
                return new int[] { 0, p, 0, 0 };
            }
            chars++;
            p += 2;
        }
        // A plausible Japanese dialogue literal is kana-heavy; a stream of ASCII
        // byte-pairs decoded as CJK ideographs has almost no kana.
        if (!terminated || cjk < 4 || chars < cjk || kana < 2 || kana * 3 < cjk) {
            return new int[] { 0, p, 0, 0 };
        }
        // Reject a match that starts in the middle of a longer valid run.
        if (i >= 2) {
            int prev = (buf[i - 2] & 0xff) | ((buf[i - 1] & 0xff) << 8);
            if (isCjk(prev) || (prev >= 0x20 && prev <= 0x7e)) {
                return new int[] { 0, p, 0, 0 };
            }
        }
        return new int[] { 1, p, cjk, chars };
    }

    /** {valid, cjkCount, charCount} for the UTF-16LE run starting at byte offset i. */
    private int[] decodeUtf16Run(byte[] buf, int i) {
        int cjk = 0;
        int chars = 0;
        int max = Math.min(buf.length - 1, i + 2400);
        int p = i;
        while (p + 1 < max) {
            int u = (buf[p] & 0xff) | ((buf[p + 1] & 0xff) << 8);
            p += 2;
            if (u == 0) {
                break;
            }
            if (isCjk(u)) {
                cjk++;
                chars++;
            } else if (u >= 0x20 && u <= 0x7e) {
                chars++;
            } else if ((u >= 0x3000 && u <= 0x303f) || (u >= 0xff00 && u <= 0xffef)
                    || u == 0x2018 || u == 0x2019 || u == 0x201c || u == 0x201d
                    || u == 0x2026 || u == 0x2014 || u == 0x2665 || u == 0x266a
                    || u == 0x2190 || u == 0x2191 || u == 0x2192 || u == 0x2193) {
                chars++;
            } else {
                break;
            }
        }
        return new int[] { cjk >= 3 ? 1 : 0, cjk, chars };
    }

    private static boolean isCjk(int u) {
        return (u >= 0x3040 && u <= 0x30ff) || (u >= 0x3400 && u <= 0x4dbf)
                || (u >= 0x4e00 && u <= 0x9fff) || (u >= 0xff65 && u <= 0xff9f)
                || (u >= 0xac00 && u <= 0xd7af);
    }

    /** Common punctuation/symbols allowed inside a Japanese dialogue literal. */
    private static boolean isTextPunct(int u) {
        return u == 0x2018 || u == 0x2019 || u == 0x201c || u == 0x201d
                || u == 0x2026 || u == 0x2014 || u == 0x2665 || u == 0x266a
                || (u >= 0x2190 && u <= 0x2193);
    }

    private static int countCjk(String s) {
        int n = 0;
        for (int i = 0; i < s.length();) {
            int cp = s.codePointAt(i);
            if (isCjk(cp)) {
                n++;
            }
            i += Character.charCount(cp);
        }
        return n;
    }

    private static String truncate(String s, int max) {
        String t = s.replace('\r', ' ').replace('\n', ' ').trim();
        return t.length() <= max ? t : t.substring(0, max) + "...";
    }

    /**
     * Detect a CLR image even when it has no native import table (pure IL assemblies):
     * the CLI metadata root starts with the ASCII signature "BSJB"; classic mixed-mode
     * binaries also carry "mscoree.dll". Scanning bytes is cheap and loader-independent.
     */
    private static boolean looksDotnet(Memory memory) {
        byte[] bsjb = "BSJB".getBytes(StandardCharsets.US_ASCII);
        byte[] mscoree = "mscoree.dll".getBytes(StandardCharsets.US_ASCII);
        for (MemoryBlock b : memory.getBlocks()) {
            if (!b.isInitialized() || b.getSize() > 16L * 1024 * 1024) {
                continue;
            }
            byte[] buf;
            try {
                buf = new byte[(int) b.getSize()];
                memory.getBytes(b.getStart(), buf);
            } catch (Exception e) {
                continue;
            }
            if (indexOf(buf, bsjb) >= 0 || indexOf(buf, mscoree) >= 0) {
                return true;
            }
        }
        return false;
    }

    private static int indexOf(byte[] hay, byte[] needle) {
        outer:
        for (int i = 0; i + needle.length <= hay.length; i++) {
            for (int j = 0; j < needle.length; j++) {
                if (hay[i + j] != needle[j]) {
                    continue outer;
                }
            }
            return i;
        }
        return -1;
    }

    private static String sha256Of(String path) {
        try {
            File f = new File(path);
            if (!f.isFile()) {
                return "";
            }
            MessageDigest md = MessageDigest.getInstance("SHA-256");
            byte[] d = md.digest(Files.readAllBytes(f.toPath()));
            StringBuilder h = new StringBuilder();
            for (byte x : d) {
                h.append(String.format("%02x", x));
            }
            return h.toString();
        } catch (Exception e) {
            return "";
        }
    }

    // ---- tiny JSON writer -------------------------------------------------------

    private void indent(int level) {
        for (int i = 0; i < level; i++) {
            sb.append("  ");
        }
    }

    private void field(String k, String v, boolean comma, int level) {
        indent(level);
        sb.append(q(k)).append(": ").append(q(v));
        sb.append(comma ? ",\n" : "\n");
    }

    private void field(String k, long v, boolean comma, int level) {
        indent(level);
        sb.append(q(k)).append(": ").append(v);
        sb.append(comma ? ",\n" : "\n");
    }

    private void field(String k, boolean v, boolean comma, int level) {
        indent(level);
        sb.append(q(k)).append(": ").append(v);
        sb.append(comma ? ",\n" : "\n");
    }

    private void obj(String k, int level) {
        indent(level);
        sb.append(q(k)).append(": {\n");
    }

    private void close(boolean comma, int level) {
        indent(level);
        sb.append("}");
        sb.append(comma ? ",\n" : "\n");
    }

    private void arrayStart(String k, int level) {
        indent(level);
        sb.append(q(k)).append(": [\n");
    }

    private void strArray(String k, List<String> values, int level, boolean comma) {
        arrayStart(k, level);
        for (int i = 0; i < values.size(); i++) {
            if (i > 0) {
                sb.append(", ");
            }
            sb.append(q(values.get(i)));
        }
        sb.append("\n");
        indent(level);
        sb.append("]").append(comma ? ",\n" : "\n");
    }

    private static String q(String s) {
        if (s == null) {
            return "\"\"";
        }
        StringBuilder b = new StringBuilder(s.length() + 2);
        b.append('"');
        for (int i = 0; i < s.length(); i++) {
            char c = s.charAt(i);
            switch (c) {
                case '"': b.append("\\\""); break;
                case '\\': b.append("\\\\"); break;
                case '\n': b.append("\\n"); break;
                case '\r': b.append("\\r"); break;
                case '\t': b.append("\\t"); break;
                default:
                    if (c < 0x20) {
                        b.append(String.format("\\u%04x", (int) c));
                    } else {
                        b.append(c);
                    }
            }
        }
        b.append('"');
        return b.toString();
    }
}
