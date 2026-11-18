import serial, time, struct
s = serial.Serial("COM3", 115200, timeout=0.3, write_timeout=1)
time.sleep(0.2)
d = s.read(256); print("drained:", d[:40], "len:", len(d))
cmd=b"\xa0\x0a\x50\x05"; i=0; t=0
while t<300:
    t+=1
    s.write(bytes([cmd[i]])); s.flush()
    v=s.read(1)
    if len(v)==1 and v[0]==(~cmd[i]&0xFF):
        i+=1
        if i==4: break
    else: i=0
print("handshake:", i==4, "tries:", t)
if i==4:
    s.write(b"\xfd"); s.flush(); time.sleep(0.1); r=s.read(8); print("hwcode raw:", r.hex())
s.close()
