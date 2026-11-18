import usb.core, usb.util, usb.backend.libusb1, traceback
dll = r"D:\DSHWorkSpace\tools\mtkclient-main\mtkclient\Windows\libusb-1.0.dll"
be = usb.backend.libusb1.get_backend(find_library=lambda x: dll)
dev = usb.core.find(idVendor=0x22d9, idProduct=0x2769, backend=be)
try:
    usb.util.claim_interface(dev, 0)
    print("claim ok")
except Exception as e:
    print("claim err:", e)
ep_out = usb.util.find_descriptor(dev[0][(0,0)], custom_match=lambda e: usb.util.endpoint_direction(e.bEndpointAddress)==usb.util.ENDPOINT_OUT and usb.util.endpoint_type(e.bmAttributes)==usb.util.ENDPOINT_TYPE_BULK)
ep_in  = usb.util.find_descriptor(dev[0][(0,0)], custom_match=lambda e: usb.util.endpoint_direction(e.bEndpointAddress)==usb.util.ENDPOINT_IN and usb.util.endpoint_type(e.bmAttributes)==usb.util.ENDPOINT_TYPE_BULK)
print("ep_out", ep_out, "ep_in", ep_in)
cmd = b"\xa0\x0a\x50\x05"
for b in cmd:
    try:
        n = ep_out.write(bytes([b]), timeout=1000)
    except Exception as e:
        print("write %02x err: %s" % (b, e)); break
    try:
        v = ep_in.read(1, timeout=1000)
        print("sent %02x -> read %r" % (b, bytes(v)))
    except Exception as e:
        print("sent %02x -> read err: %s" % (b, e))
print("done")
