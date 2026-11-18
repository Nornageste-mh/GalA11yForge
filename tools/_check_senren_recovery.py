import csv, hashlib, os, sys

GAME = r'D:\Steam\steamapps\common\SenrenBanka'
SNAP = r'D:\DSHWorkSpace\research\45-kkz-adapter-e2e\evidence\snapshot-before.csv'

missing, mismatch, ok = [], [], 0
with open(SNAP, encoding='utf-8-sig', newline='') as f:
    for row in csv.DictReader(f):
        rel = row['relpath']
        scope = row['scope']
        path = os.path.join(GAME, scope, rel) if scope in ('plugin', 'root') else os.path.join(GAME, rel)
        if scope == 'root':
            path = os.path.join(GAME, rel)
        else:
            path = os.path.join(GAME, scope, rel)
        exp_len = int(row['bytes'])
        exp_hash = row['sha256'].upper()
        if not os.path.exists(path):
            missing.append((scope, rel, exp_len, exp_hash))
            continue
        actual_len = os.path.getsize(path)
        with open(path, 'rb') as fh:
            actual_hash = hashlib.sha256(fh.read()).hexdigest().upper()
        if actual_len != exp_len or actual_hash != exp_hash:
            mismatch.append((scope, rel, exp_len, actual_len, exp_hash, actual_hash))
        else:
            ok += 1

print(f'OK={ok}  MISSING={len(missing)}  MISMATCH={len(mismatch)}')
print('--- MISSING ---')
for m in missing:
    print('|'.join(str(x) for x in m))
print('--- MISMATCH ---')
for m in mismatch:
    print('|'.join(str(x) for x in m))
