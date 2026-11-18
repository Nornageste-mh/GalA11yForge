import sys, os, traceback, logging
sys.path.insert(0, r"D:\DSHWorkSpace\tools\mtkclient-main")
from mtkclient.config.mtk_config import MtkConfig
from mtkclient.Library.mtk_class import Mtk
cfg = MtkConfig(loglevel=10)
mtk = Mtk(config=cfg, loglevel=10, serialportname="COM3")
print("port cdc:", type(mtk.port.cdc).__name__, "| serialportname:", mtk.port.serialportname)
try:
    r = mtk.port.cdc.connect()
    print("connect ->", r, "| connected:", mtk.port.cdc.connected)
except Exception:
    traceback.print_exc()
