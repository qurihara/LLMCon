"""別のパソコンをまたいだ接続を確かめる（2026/6/21 以来の宿題）。

1P は栗原オフィスの Windows 機で動く LLMCon。
2P は研究室の mac で動くホスト。
1P の人間入力で事象を検出し、2P へ作用を送る。
"""
import re, time
from llmcon import Mcp, VCon

WIN = "100.119.199.18"      # 1P（栗原オフィス・Windows）
MAC = "100.69.63.106"       # 2P（研究室・mac）

m1 = Mcp(WIN, name="mac-session-1P")
m2 = Mcp(MAC, name="mac-session-2P")
v1 = VCon(WIN)              # 1P の人間入力（事象を起こす側）
v2 = VCon(MAC)              # 2P の人間入力（作用を受ける側）


def btns(m):
    return re.search(r"buttons=\[([^\]]*)\]", m.state()).group(1)


def clean():
    for mm in (m1, m2):
        for c in re.findall(r"^(c\d+):", mm.call("list_connections"), re.M):
            mm.call("remove_connection", id=c)
        mm.call("set_mapping", rules=[])
    time.sleep(0.4)
    v1.recv(0.2); v2.recv(0.2)


print("=" * 68)
print("両方の機械の状態")
print("  1P (Windows / 栗原オフィス):", m1.call("get_info"))
print("  2P (mac / 研究室)          :", m2.call("get_info"))

clean()

print()
print("=" * 68)
print("接続を登録する（1P 側に登録する。事象を検出するのは 1P だから）")
r = m1.call("add_connection",
            event={"type": "press", "button": "A"},
            target={"host": MAC, "port": 8777},
            action={"kind": "mapping", "durationSec": 1.0,
                    "rules": [{"op": "disable", "button": "A"}]})
print(" ", r)
print(" ", m1.call("list_connections"))

print()
print("=" * 68)
print("まず、事象を起こす前の 2P の様子を見る")
v2.press("A"); time.sleep(0.3)
print(f"  2P で A を押した → 2P の出力 buttons=[{btns(m2)}]   （A が出るのが正しい）")
v2.release("A"); time.sleep(0.3)

print()
print("=" * 68)
print("1P で人間入力の A を押す（ここで事象が発火し、2P へ作用が飛ぶ）")
t0 = time.perf_counter()
v1.press("A")
time.sleep(0.15)
v1.release("A")
print(f"  1P の出力 buttons=[{btns(m1)}]")

print()
print("  作用が届いた直後に、2P で A を押してみる")
v2.press("A"); time.sleep(0.3)
b = btns(m2)
elapsed = (time.perf_counter() - t0) * 1000
print(f"  → 2P の出力 buttons=[{b}]   （'-' なら作用が届いている）")
print(f"  → 2P の状態: {m2.state()}")
v2.release("A")

print()
print("  1.2秒待って、作用が切れるのを確かめる")
time.sleep(1.2)
v2.press("A"); time.sleep(0.3)
print(f"  → 2P の出力 buttons=[{btns(m2)}]   （A に戻るのが正しい）")
v2.release("A")

print()
print("=" * 68)
print("事象の記録（両側）")
print()
print("  1P 側（送った記録が残るはず）")
print("   ", m1.call("get_events", count=4).replace("\n", "\n    "))
print()
print("  2P 側（受け取った記録が残るはず。誰から来たかも分かる）")
print("   ", m2.call("get_events", count=4).replace("\n", "\n    "))
print()
print("  接続の発火回数")
print("   ", m1.call("list_connections"))

clean()
v1.close(); v2.close()
print()
print("後始末: 接続と改変ルールを両側から消した")
