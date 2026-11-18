import sys, argparse
sys.path.insert(0, r"D:\DSHWorkSpace\tools\mtkclient-main")
sys.argv=["mtk.py","printgpt","--serialport","COM3","--loglevel","DEBUG"]
import mtk as m
base=m.create_base_parser()
p=argparse.ArgumentParser(parents=[base], add_help=False)
sub=p.add_subparsers(dest="cmd", required=True, metavar="command")
sub.add_parser("printgpt", parents=[base])
a=p.parse_args()
print("serialport =", repr(getattr(a,"serialport",None)), "| cmd =", a.cmd)
