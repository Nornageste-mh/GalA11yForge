import sys, usb.core, usb.util
devs = list(usb.core.find(find_all=True))
for d in devs:
    print(hex(d.idVendor)+":"+hex(d.idProduct), "bcdUSB=%x"%d.bcdUSB, "class=%x"%d.bDeviceClass, "sub=%x"%d.bDeviceSubClass, "proto=%x"%d.bDeviceProtocol, "numcfg=%d"%d.bNumConfigurations)
    for cfg in d:
        print("   cfg %d: intf=%d selfpowered=%s" % (cfg.bConfigurationValue, cfg.bNumInterfaces, cfg.self_powered))
        for intf in cfg:
            print("      intf %d alt %d class=%x sub=%x proto=%x eps=%d" % (intf.bInterfaceNumber, intf.bAlternateSetting, intf.bInterfaceClass, intf.bInterfaceSubClass, intf.bInterfaceProtocol, intf.bNumEndpoints))
