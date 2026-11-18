import usb.core, usb.backend.libusb1, traceback
dll = r"D:\DSHWorkSpace\tools\mtkclient-main\mtkclient\Windows\libusb-1.0.dll"
be = usb.backend.libusb1.get_backend(find_library=lambda x: dll)
print("backend:", be)
if be is None:
    raise SystemExit("no backend")
devs = list(usb.core.find(find_all=True, backend=be))
for d in devs:
    mark = "  <== TARGET" if (d.idVendor==0x22d9) else ""
    print("%04x:%04x bus=%s addr=%s class=%02x sub=%02x proto=%02x%s" % (d.idVendor, d.idProduct, d.bus, d.address, d.bDeviceClass, d.bDeviceSubClass, d.bDeviceProtocol, mark))
tgt = usb.core.find(idVendor=0x22d9, idProduct=0x2769, backend=be)
print("target:", tgt)
if tgt is not None:
    try:
        for cfg in tgt:
            print("cfg", cfg.bConfigurationValue, "intf", cfg.bNumInterfaces)
            for intf in cfg:
                print("   interface", intf.bInterfaceNumber, "class=%02x sub=%02x proto=%02x eps=%d" % (intf.bInterfaceClass, intf.bInterfaceSubClass, intf.bInterfaceProtocol, intf.bNumEndpoints))
                for ep in intf:
                    print("      ep %02x attr=%02x maxpkt=%d" % (ep.bEndpointAddress, ep.bmAttributes, ep.wMaxPacketSize))
    except Exception as e:
        print("descriptor read err:", e)
