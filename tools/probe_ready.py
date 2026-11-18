import serial, time
try:
    s=serial.Serial("COM3",115200,timeout=0.6)
    time.sleep(0.2); d=s.read(64); print("read:", d)
    s.close()
except Exception as e:
    print("ERR", e)
