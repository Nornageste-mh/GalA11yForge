import sys
sys.path.insert(0, r"D:\DSHWorkSpace\tools\mtkclient-main")
import usb.core, usb.backend.libusb1
dll = r"D:\DSHWorkSpace\tools\mtkclient-main\mtkclient\Windows\libusb-1.0.dll"
be = usb.backend.libusb1.get_backend(find_library=lambda x: dll)
devs = list(usb.core.find(find_all=True, backend=be) or [])
print("total:", len(devs))
for d in devs:
    try:
        print(f"{d.idVendor:04x}:{d.idProduct:04x} bus {d.bus} addr {d.address} class {d.bDeviceClass:02x}")
    except Exception as e:
        print("err", e)
