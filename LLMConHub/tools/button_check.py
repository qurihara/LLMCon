# Switch のボタンの動作チェックの画面（または Steam のテストの画面）に、番号つきの手順で入力を流す。
# 使い方：python3 button_check.py http://<LLMCon Hub の番地>:8777/
# ボタンは0.3秒だけ押す。長く押すと、Switch 2 のボタンの動作チェックの画面を抜けてしまう（2026-11-03 に確かめた）。
# スティックは1秒倒す。見た人は、画面に出なかった手順の番号だけを記録すればよい。
import json, time, urllib.request
import sys
URL = sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:8777/"
H = {"Content-Type": "application/json", "Accept": "application/json, text/event-stream"}
sid = None
def post(body):
    global sid
    h = dict(H)
    if sid: h["Mcp-Session-Id"] = sid
    r = urllib.request.urlopen(urllib.request.Request(URL, data=json.dumps(body).encode(), headers=h, method="POST"), timeout=10)
    sid = r.headers.get("Mcp-Session-Id") or sid
    return r.read().decode()
post({"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"sw2full","version":"0"}}})
post({"jsonrpc":"2.0","method":"notifications/initialized"})
n=[10]
def call(name, args):
    n[0]+=1
    t=post({"jsonrpc":"2.0","id":n[0],"method":"tools/call","params":{"name":name,"arguments":args}})
    if '"isError":true' in t: print("  誤り", name, args, t[:200], flush=True)
def hold(b): call("hold", {"buttons": b})
def rel(b): call("release", {"buttons": b})
HOLD_BTN=0.3; HOLD_STICK=1.0; GAP=0.8
steps = [
 ("Switch の A", lambda: hold(["B"]), lambda: rel(["B"])),
 ("Switch の B", lambda: hold(["A"]), lambda: rel(["A"])),
 ("Switch の X", lambda: hold(["Y"]), lambda: rel(["Y"])),
 ("Switch の Y", lambda: hold(["X"]), lambda: rel(["X"])),
 ("L", lambda: hold(["LB"]), lambda: rel(["LB"])),
 ("R", lambda: hold(["RB"]), lambda: rel(["RB"])),
 ("ZL", lambda: call("set_trigger", {"side":"left","value":1.0}), lambda: call("set_trigger", {"side":"left","value":0.0})),
 ("ZR", lambda: call("set_trigger", {"side":"right","value":1.0}), lambda: call("set_trigger", {"side":"right","value":0.0})),
 ("−", lambda: hold(["Back"]), lambda: rel(["Back"])),
 ("＋", lambda: hold(["Start"]), lambda: rel(["Start"])),
 ("左スティックの押し込み", lambda: hold(["LS"]), lambda: rel(["LS"])),
 ("右スティックの押し込み", lambda: hold(["RS"]), lambda: rel(["RS"])),
 ("十字キーの上", lambda: hold(["DUp"]), lambda: rel(["DUp"])),
 ("十字キーの右", lambda: hold(["DRight"]), lambda: rel(["DRight"])),
 ("十字キーの下", lambda: hold(["DDown"]), lambda: rel(["DDown"])),
 ("十字キーの左", lambda: hold(["DLeft"]), lambda: rel(["DLeft"])),
 ("左スティックを上", lambda: call("set_stick", {"side":"left","x":0,"y":1}), lambda: call("set_stick", {"side":"left","x":0,"y":0})),
 ("左スティックを右", lambda: call("set_stick", {"side":"left","x":1,"y":0}), lambda: call("set_stick", {"side":"left","x":0,"y":0})),
 ("左スティックを下", lambda: call("set_stick", {"side":"left","x":0,"y":-1}), lambda: call("set_stick", {"side":"left","x":0,"y":0})),
 ("左スティックを左", lambda: call("set_stick", {"side":"left","x":-1,"y":0}), lambda: call("set_stick", {"side":"left","x":0,"y":0})),
 ("右スティックを上", lambda: call("set_stick", {"side":"right","x":0,"y":1}), lambda: call("set_stick", {"side":"right","x":0,"y":0})),
 ("右スティックを右", lambda: call("set_stick", {"side":"right","x":1,"y":0}), lambda: call("set_stick", {"side":"right","x":0,"y":0})),
 ("右スティックを下", lambda: call("set_stick", {"side":"right","x":0,"y":-1}), lambda: call("set_stick", {"side":"right","x":0,"y":0})),
 ("右スティックを左", lambda: call("set_stick", {"side":"right","x":-1,"y":0}), lambda: call("set_stick", {"side":"right","x":0,"y":0})),
 ("A と X と L の同時押し", lambda: hold(["B","Y","LB"]), lambda: rel(["B","Y","LB"])),
]
for i,(name,on,off) in enumerate(steps,1):
    print(time.strftime("%T"), f"手順 {i:2d}：{name}", flush=True)
    on(); time.sleep(HOLD_STICK if 'スティックを' in name else HOLD_BTN); off(); time.sleep(GAP)
call("release_all", {})
print(time.strftime("%T"), "終わり", flush=True)
