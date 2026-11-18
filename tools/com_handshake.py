import serial, time, sys
try:
    s = serial.Serial("COM3", 115200, bytesize=8, parity="N", stopbits=1, timeout=1, write_timeout=1)
except Exception as e:
    print("open err:", e); sys.exit(1)
print("opened", s.name, "is_open", s.is_open)
cmd = b"\xa0\x0a\x50\x05"
ok = True
for b in cmd:
    s.write(bytes([b]))
    s.flush()
    v = s.read(1)
    print("sent %02x -> %r" % (b, v))
    if v != bytes([b ^ 0xFF]):
        ok = False
        break
print("HANDSHAKE", "OK" if ok else "FAILED")
if ok:
    # read hw code command: BROM_GET_HW_CODE 0xFD? preloader uses 0xD8? leave for mtkclient
    pass
s.close()
