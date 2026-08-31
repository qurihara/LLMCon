"""WebSocket の出力シンク（Issue #2）を、mac から遠隔で確かめる。

要点は「配られるのが改変ルールを適用した後の状態か」である。もし改変前の状態が
配られていれば、ブラウザのゲームで改変の効果を見るという目的を果たさない。
"""
import json, re, time
from llmcon import Mcp, VCon

H = "100.119.199.18"
m = Mcp(H)
v = VCon(H)


def latest_pad(timeout=0.6):
    """直近に届いたコントローラの状態を返す。"""
    got = v.pads(timeout)
    return got[-1] if got else None


def clear():
    m.call("set_mapping", rules=[])
    v.stick("left", 0, 0)
    time.sleep(0.4)
    v.recv(0.2)          # 溜まっているものを捨てる


print("=" * 66)
print("0 出力先の確認")
print("  ", m.call("get_info"))

print()
print("=" * 66)
print("1 配信の形と、人間入力が届くか")
clear()
v.press("A"); v.press("DRight")
p = latest_pad()
print(f"  A と DRight を押した → {json.dumps(p, ensure_ascii=False)}")
v.release("A"); v.release("DRight"); time.sleep(0.3)
v.recv(0.3)
v.stick("left", 0.75, -0.5)
p = latest_pad()
print(f"  左スティック(0.75,-0.5) → lx={p.get('lx') if p else '?'} ly={p.get('ly') if p else '?'}")
clear()

print()
print("=" * 66)
print("2 【核心】改変ルールを適用した後の状態が配られているか")
print()

print("  2-1 無効化（A を無効にして A を押す）")
clear()
m.call("set_mapping", rules=[{"op": "disable", "button": "A"}])
time.sleep(0.3); v.recv(0.2)
v.press("A"); v.press("B")
p = latest_pad()
print(f"      A と B を押した → buttons={p.get('buttons') if p else '?'}")
print(f"      （B だけなら改変後の状態が配られている。A と B の両方なら改変前）")
v.release("A"); v.release("B")

print()
print("  2-2 反転（何も押していないのに X が出るか）")
clear()
m.call("set_mapping", rules=[{"op": "invert", "button": "X"}])
time.sleep(0.5)
p = latest_pad(1.5)
print(f"      何も押していない → buttons={p.get('buttons') if p else '(届かず)'}")
print(f"      （X が入っていれば、反転の効果が受け手に現れている）")

print()
print("  2-3 アナログ変換（感度 0.5 で左スティックを右いっぱいに）")
clear()
v.stick("left", 1.0, 0.0); time.sleep(0.3); v.recv(0.2)
p = latest_pad()
before = p.get("lx") if p else None
m.call("set_mapping", rules=[{"op": "gain", "axis": "LS", "amount": 0.5}])
time.sleep(0.5)
p = latest_pad(1.5)
after = p.get("lx") if p else None
print(f"      改変なし lx={before} → 感度0.5 lx={after}")
print(f"      （半分になっていれば、アナログ変換も受け手に現れている）")

print()
print("  2-4 反応遅延（300ミリ秒。押してから配信に現れるまでを測る）")
clear()
m.call("set_mapping", rules=[{"op": "delay", "delayMs": 300}])
time.sleep(0.5); v.recv(0.3)
t0 = time.perf_counter()
v.press("Y")
seen = None
while time.perf_counter() - t0 < 2.0:
    for p in v.pads(0.05):
        if "Y" in (p.get("buttons") or []):
            seen = (time.perf_counter() - t0) * 1000
            break
    if seen:
        break
print(f"      押してから配信に現れるまで {seen:.0f} ミリ秒" if seen else "      現れなかった")
print(f"      （300ミリ秒前後なら、反応遅延も受け手に現れている）")
v.release("Y")
clear()

print()
print("=" * 66)
print("3 配信の頻度（10秒間、何件届くか。上限は毎秒60回のはず）")
clear()
t0 = time.time()
n_pad = 0
n_other = 0
kinds = {}
while time.time() - t0 < 10:
    for msg in v.recv(0.2):
        try:
            o = json.loads(msg)
            k = o.get("t") if isinstance(o, dict) else "(配列など)"
        except Exception:
            k = f"(JSONでない: {msg[:20]})"
        kinds[k] = kinds.get(k, 0) + 1
        if k == "pad":
            n_pad += 1
        else:
            n_other += 1
    # 何もしない時間を作る（変化が無くても1秒に1回は届く作りのはず）
print(f"  10秒間、何も操作しない → pad が {n_pad} 件")
print(f"  （変化が無くても1秒に1回送る作りなので、10件前後が期待値）")
print(f"  届いた種別の内訳: {kinds}")

clear()
v.close()
print()
print("後始末: 改変ルールを消した")
