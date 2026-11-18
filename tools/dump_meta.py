import sys
sys.path.insert(0, r"D:\DSHWorkSpace\tools\mtkclient-main")
import usb.core, usb.backend.libusb1
dll = r"D:\DSHWorkSpace\tools\mtkclient-main\mtkclient\Windows\libusb-1.0.dll"
be = usb.backend.libusb1.get_backend(find_library=lambda x: dll)
print("backend:", be)
for vid,pid in [(0x0e8d,0x2046),(0x22d9,0x2046)]:
    dev = usb.core.find(idVendor=vid, idProduct=pid, backend=be)
    print(f"--- {vid:04x}:{pid:04x} ->", dev)
    if dev is None: continue
    print("  mfr:", dev.manufacturer, "| product:", dev.product, "| serial:", dev.serial_number)
    for cfg in dev:
        print("  config", cfg.bConfigurationValue)
        for intf in cfg:
            print(f"    intf {intf.bInterfaceNumber} alt {intf.bAlternateSetting} class {intf.bInterfaceClass:02x} sub {intf.bInterfaceSubClass:02x} proto {intf.bInterfaceProtocol:02x}")
            for ep in intf:
                print(f"      ep {ep.bEndpointAddress:02x} attr {ep.bmAttributes:02x} maxpkt {ep.wMaxPacketSize}")
