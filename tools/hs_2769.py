import sys, time
sys.path.insert(0, r"D:\DSHWorkSpace\tools\mtkclient-main")
import usb.core, usb.backend.libusb1, usb.util
dll = r"D:\DSHWorkSpace\tools\mtkclient-main\mtkclient\Windows\libusb-1.0.dll"
be = usb.backend.libusb1.get_backend(find_library=lambda x: dll)
dev = usb.core.find(idVendor=0x22d9, idProduct=0x2769, backend=be)
print("dev:", dev)
if dev is not None:
    try:
        dev.set_configuration()
    except Exception as e:
        print("set_configuration:", e)
    cfg = dev.get_active_configuration()
    intf = cfg[(0,0)]
    print("intf:", intf.bInterfaceNumber, intf.bInterfaceClass, intf.bInterfaceSubClass, intf.bInterfaceProtocol)
    try:
        usb.util.claim_interface(dev, 0)
        print("claimed")
    except Exception as e:
        print("claim err:", e)
    ep_out = usb.util.find_descriptor(intf, custom_match=lambda e: usb.util.endpoint_direction(e.bEndpointAddress)==usb.util.ENDPOINT_OUT)
    ep_in  = usb.util.find_descriptor(intf, custom_match=lambda e: usb.util.endpoint_direction(e.bEndpointAddress)==usb.util.ENDPOINT_IN)
    print("ep_out", ep_out.bEndpointAddress, "ep_in", ep_in.bEndpointAddress)
    cmd = b"\xa0\x0a\x50\x05"
    i = 0; t = 0
    while t < 300:
        t += 1
        try:
            ep_out.write(bytes([cmd[i]]), timeout=200)
        except Exception as e:
            print("w err", e); break
        try:
            v = ep_in.read(1, timeout=150)
            b = v[0]
        except Exception as e:
            b = None
        if b is not None and b == ((~cmd[i]) & 0xFF):
            i += 1
            if i == 4: break
        else:
            i = 0
    print("handshake:", i == 4, "tries:", t)
    if i == 4:
        ep_out.write(b"\xfd", timeout=200); time.sleep(0.05)
        try:
            r = bytes(ep_in.read(8, timeout=300))
            print("hwcode raw:", r.hex())
        except Exception as e:
            print("read err", e)
