import usb.core, usb.util, usb.backend.libusb1, time
dll = r"D:\DSHWorkSpace\tools\mtkclient-main\mtkclient\Windows\libusb-1.0.dll"
be = usb.backend.libusb1.get_backend(find_library=lambda x: dll)
dev = usb.core.find(idVendor=0x22d9, idProduct=0x2769, backend=be)
print("found:", dev is not None)
for attr in ("manufacturer","product","serial_number"):
    try:
        print(attr, "=", getattr(dev, attr))
    except Exception as e:
        print(attr, "err", e)
try:
    print("bcdDevice:", hex(dev.bcdDevice), "bcdUSB:", hex(dev.bcdUSB))
except Exception as e:
    print("desc err", e)
try:
    usb.util.claim_interface(dev, 0)
except Exception as e:
    print("claim err:", e)
    raise SystemExit(0)
# try single 4-byte bulk write handshake
cmd = b"\xa0\x0a\x50\x05"
ep_out = usb.util.find_descriptor(dev[0][(0,0)], custom_match=lambda e: usb.util.endpoint_direction(e.bEndpointAddress)==0)
ep_in  = usb.util.find_descriptor(dev[0][(0,0)], custom_match=lambda e: usb.util.endpoint_direction(e.bEndpointAddress)!=0)
for label, payload, nread in (("bytewise-4B", cmd, 4), ("single-4B", cmd, 4)):
    try:
        ep_out.write(payload, timeout=800)
    except Exception as e:
        print(label, "write err", e); continue
    try:
        v = ep_in.read(nread, timeout=800)
        print(label, "->", bytes(v).hex())
    except Exception as e:
        print(label, "read err", e)
# dump a zero-length read to flush
try:
    v = ep_in.read(64, timeout=300)
    print("flush got", bytes(v).hex())
except Exception as e:
    print("flush err", e)
