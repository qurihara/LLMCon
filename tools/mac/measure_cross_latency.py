"""機械をまたいだ接続の遅延を測る。

測るのは「1P で人間入力の事象が起きてから、2P にその作用が適用されるまで」。
mac 側の時計だけで測れるように、次の形にした。

  t0 : mac から 1P（Windows）へ、ボタンの押下を送った時刻
  t1 : mac のホスト（2P）で、接続由来の改変ルールが増えたのを検出した時刻

t1 − t0 には、mac から Windows への送信（片道）、Windows 側での事象の検出、
Windows から mac への作用の送信（片道）、mac 側での適用、が含まれる。
mac 側の観測は同じ機械の中なので、ほぼ 0 と見てよい。

実際のプレイでは 1P の人間は手元のパッドを押すので、最初の片道は乗らない。
その分を差し引いた値が、実運用に近い。片道はおよそ往復の半分である。
"""
import re, statistics, time
from llmcon import Mcp, VCon

WIN = "100.119.199.18"
MAC = "100.69.63.106"

m1 = Mcp(WIN, name="lat-1P")
m2 = Mcp(MAC, name="lat-2P")
v1 = VCon(WIN)


def conn_count():
    return int(re.search(r"conn=(\d+)", m2.state()).group(1))


def clean():
    for c in re.findall(r"^(c\d+):", m1.call("list_connections"), re.M):
        m1.call("remove_connection", id=c)
    m1.call("set_mapping", rules=[])
    time.sleep(0.3)


clean()
m1.call("add_connection",
        event={"type": "press", "button": "A"},
        target={"host": MAC, "port": 8777},
        action={"kind": "mapping", "durationSec": 0.3,
                "rules": [{"op": "disable", "button": "A"}]})

print("1P（Windows・栗原オフィス）で A を押してから、")
print("2P（mac・研究室）に作用が適用されるまでを測る。")
print()

# 参考値として、経路の往復も測っておく
import subprocess
ts = "/Applications/Tailscale.app/Contents/MacOS/Tailscale"
try:
    out = subprocess.run([ts, "ping", "-c", "3", WIN], capture_output=True, text=True, timeout=15).stdout
    rtts = [float(x) for x in re.findall(r"in (\d+)ms", out)]
    print(f"参考: Tailscale の往復 {statistics.median(rtts):.0f} ミリ秒（片道はおよそその半分）")
except Exception:
    pass
print()

results = []
for i in range(8):
    while conn_count() != 0:
        time.sleep(0.1)
    time.sleep(0.3)
    t0 = time.perf_counter()
    v1.press("A")
    hit = None
    while time.perf_counter() - t0 < 3.0:
        if conn_count() > 0:
            hit = (time.perf_counter() - t0) * 1000
            break
    v1.release("A")
    if hit:
        results.append(hit)
        print(f"  {i+1}回目: {hit:6.1f} ミリ秒")
    else:
        print(f"  {i+1}回目: 検出できず")
    time.sleep(0.5)

print()
if results:
    print(f"中央値 {statistics.median(results):.1f} ミリ秒 / "
          f"最小 {min(results):.1f} / 最大 {max(results):.1f} （{len(results)}回）")
    print()
    print("この値には、mac から Windows へ押下を送る片道が含まれている。")
    print("実際のプレイでは 1P の人間が手元で押すので、その片道は乗らない。")

clean()
v1.close()
print()
print("後始末: 接続を消した")
