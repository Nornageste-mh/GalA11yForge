import ctypes, sys
setupapi = ctypes.WinDLL("setupapi", use_last_error=True)
class GUID(ctypes.Structure):
    _fields_ = [("Data1", ctypes.c_uint32),("Data2", ctypes.c_uint16),("Data3", ctypes.c_uint16),("Data4", ctypes.c_ubyte*8)]
class SP_DID(ctypes.Structure):
    _fields_ = [("cbSize", ctypes.c_uint32), ("InterfaceClassGuid", GUID), ("Flags", ctypes.c_uint32), ("Reserved", ctypes.c_void_p)]
class SP_DETAIL(ctypes.Structure):
    _fields_ = [("cbSize", ctypes.c_uint32), ("DevicePath", ctypes.c_wchar*1024)]
g = GUID(0xA5DCBF10, 0x6530, 0x11D2, (ctypes.c_ubyte*8)(0x90,0x1F,0x00,0xC0,0x4F,0xB9,0x51,0xED))
h = setupapi.SetupDiGetClassDevsW(ctypes.byref(g), None, None, 0x02|0x10)
print("hdevinfo:", h, "err:", ctypes.get_last_error())
total=0; hits=[]
while True:
    d = SP_DID(); d.cbSize = ctypes.sizeof(SP_DID)
    if not setupapi.SetupDiEnumDeviceInterfaces(h, None, ctypes.byref(g), total, ctypes.byref(d)):
        print("enum stop at", total, "err", ctypes.get_last_error()); break
    total += 1
    det = SP_DETAIL(); det.cbSize = 8
    req = ctypes.c_uint32(0)
    setupapi.SetupDiGetDeviceInterfaceDetailW(h, ctypes.byref(d), None, 0, ctypes.byref(req), None)
    if setupapi.SetupDiGetDeviceInterfaceDetailW(h, ctypes.byref(d), ctypes.byref(det), req.value, ctypes.byref(req), None):
        p = det.DevicePath
        if "22d9" in p.lower(): hits.append(p)
print("total USB interfaces:", total)
print("22d9 hits:", hits)
