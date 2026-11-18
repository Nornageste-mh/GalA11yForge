p = r"D:\DSHWorkSpace\tools\mtkclient-main\mtkclient\Library\Port.py"
s = open(p, encoding="utf-8").read()
s = s.replace("_hs_mismatch > 200", "_hs_mismatch > 60")
needle = """                else:
                    if loop == 5:"""
repl = """                else:
                    try:
                        if self.cdc.connected:
                            self.cdc.close()
                    except Exception:
                        pass
                    if loop == 5:"""
if "self.cdc.close()\n                    except Exception" not in s:
    assert needle in s
    s = s.replace(needle, repl, 1)
open(p, "w", encoding="utf-8", newline="\n").write(s)
lines = open(p, encoding="utf-8").read().splitlines()
for i,l in enumerate(lines):
    if "_hs_mismatch" in l or "self.cdc.close()" in l:
        print(i+1, l.strip())
