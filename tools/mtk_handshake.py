import usb.core, usb.util, usb.backend.libusb1, traceback, time
dll = r"D:\DSHWorkSpace\tools\mtkclient-main\mtkclient\Windows\libusb-1.0.dll"
be = usb.backend.libusb1.get_backend(find_library=lambda x: dll)
dev = usb.core.find(idVendor=0x22d9, idProduct=0x2769, backend=be)
print("dev:", dev)
for intf in dev[0]:
    print("interface", intf.bInterfaceNumber, "class=%02x sub=%02x proto=%02x" % (intf.bInterfaceClass, intf.bInterfaceSubClass, intf.bInterfaceProtocol))
    for ep in intf:
        print("   ep %02x attr=%02x maxpkt=%d" % (ep.bEndpointAddress, ep.bmAttributes, ep.wMaxPacketSize))
try:
    cfg = dev.get_active_configuration()
    print("active config:", cfg.bConfigurationValue)
except Exception as e:
    print("get_active_configuration err:", e)
try:
    dev.set_configuration()
    print("set_configuration ok")
except Exception as e:
    print("set_configuration err:", e)
# MTK BROM/preloader handshake
cmd = b"\xa0\x0a\x50\x05"
try:
    ep_in = dev[0][(0,0)][usb.util.ENDPOINT_IN]
    ep_out = dev[0][(0,0)][usb.util.ENDPOINT_OUT]
    print("eps:", hex(ep_out.bEndpointAddress), hex(ep_in.bEndpointAddress))
except Exception as e:
    print("ep lookup err:", e); raise SystemExit(0)
resp = b""
for b in cmd:
    n = ep_out.write(bytes([b]), timeout=500)
    try:
        v = ep_in.read(1, timeout=500)
        echo = bytes(v)
    except Exception as e:
        echo = b"ERR:" + str(e).encode()
    print("sent %02x -> %r" % (b, echo))
print("DONE")
