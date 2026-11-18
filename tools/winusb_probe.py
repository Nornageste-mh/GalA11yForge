import ctypes, ctypes.wintypes as w, sys
setupapi = ctypes.WinDLL("setupapi", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
class GUID(ctypes.Structure):
    _fields_ = [("Data1", ctypes.c_uint32),("Data2", ctypes.c_uint16),("Data3", ctypes.c_uint16),("Data4", ctypes.c_ubyte*8)]
class SP_DEVICE_INTERFACE_DATA(ctypes.Structure):
    _fields_ = [("cbSize", ctypes.c_uint32), ("InterfaceClassGuid", GUID), ("Flags", ctypes.c_uint32), ("Reserved", ctypes.c_void_p)]
class SP_DEVICE_INTERFACE_DETAIL_DATA_W(ctypes.Structure):
    _fields_ = [("cbSize", ctypes.c_uint32), ("DevicePath", ctypes.c_wchar*1024)]
USB_GUID = GUID(0xA5DCBF10, 0x6530, 0x11D2, (ctypes.c_ubyte*8)(0x90,0x1F,0x00,0xC0,0x4F,0xB9,0x51,0xED))
DIGCF_PRESENT = 0x02; DIGCF_DEVICEINTERFACE = 0x10
hdevinfo = setupapi.SetupDiGetClassDevsW(ctypes.byref(USB_GUID), None, None, DIGCF_PRESENT|DIGCF_DEVICEINTERFACE)
if hdevinfo == -1 or hdevinfo is None:
    print("SetupDiGetClassDevs failed", ctypes.get_last_error()); sys.exit(1)
i = 0
found=[]
while True:
    did = SP_DEVICE_INTERFACE_DATA(); did.cbSize = ctypes.sizeof(SP_DEVICE_INTERFACE_DATA)
    if not setupapi.SetupDiEnumDeviceInterfaces(hdevinfo, None, ctypes.byref(USB_GUID), i, ctypes.byref(did)):
        break
    i += 1
    detail = SP_DEVICE_INTERFACE_DETAIL_DATA_W(); detail.cbSize = 6 if ctypes.sizeof(ctypes.c_void_p)==4 else 8
    req = ctypes.c_uint32(0)
    setupapi.SetupDiGetDeviceInterfaceDetailW(hdevinfo, ctypes.byref(did), None, 0, ctypes.byref(req), None)
    if setupapi.SetupDiGetDeviceInterfaceDetailW(hdevinfo, ctypes.byref(did), ctypes.byref(detail), req.value, ctypes.byref(req), None):
        path = detail.DevicePath
        if "vid_22d9" in path.lower() or "vid_0e8d" in path.lower():
            found.append(path)
for p in found:
    print("PATH:", p)
    kernel32.CreateFileW.restype = w.HANDLE
    h = kernel32.CreateFileW(ctypes.c_wchar_p(p), 0xC0000000, 3, None, 3, 0, None)
    err = ctypes.get_last_error()
    print("  CreateFile ->", h, "err", err)
    if h is not None and h != w.HANDLE(-1).value:
        winusb = ctypes.WinDLL("winusb", use_last_error=True)
        iface = ctypes.c_void_p()
        r = winusb.WinUsb_Initialize(h, ctypes.byref(iface))
        print("  WinUsb_Initialize ->", r, "err", ctypes.get_last_error())
        kernel32.CloseHandle(h)
print("interfaces found:", len(found))
