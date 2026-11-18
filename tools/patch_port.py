import re
p = r"D:\DSHWorkSpace\tools\mtkclient-main\mtkclient\Library\Port.py"
s = open(p, encoding="utf-8").read()
if "_hs_mismatch" not in s:
    s = s.replace("        i = 0\n        startcmd = b\"\\xa0\\x0a\\x50\\x05\"",
                  "        i = 0\n        _hs_mismatch = 0\n        startcmd = b\"\\xa0\\x0a\\x50\\x05\"", 1)
    s = s.replace("                    else:\n                        i = 0\n            self.info(\"Device detected :)\")",
                  "                    else:\n                        i = 0\n                        _hs_mismatch += 1\n                        if _hs_mismatch > 200:\n                            return False\n            self.info(\"Device detected :)\")", 1)
    open(p, "w", encoding="utf-8", newline="\n").write(s)
print([ (i+1, l.strip()) for i,l in enumerate(open(p, encoding="utf-8").read().splitlines()) if "_hs_mismatch" in l ])
