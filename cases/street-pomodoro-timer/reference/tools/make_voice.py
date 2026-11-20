# -*- coding: utf-8 -*-
"""Make the spoken cues of overlay.py as WAV files with VOICEVOX (ずんだもん), once, on the lab's Mac.

overlay.py only says fixed sentences (overlay.all_sentences), so they are synthesised beforehand and the ROG Ally
plays them with winsound.PlaySound; System.Speech is used only for a sentence that has no WAV.

  python3 tools/make_voice.py                       # VOICEVOX at 127.0.0.1:50021, speaker 3 (ずんだもん ノーマル)
  python3 tools/make_voice.py --speed 1.1 --force   # make every file again
Writes voice/<hash>.wav (16 kHz mono) and voice/index.json {"sentences": {sentence: file name}, ...}.
A file is made again only when its sentence, reading, speaker, speed or rate changed (the hash covers them).
Credit (VOICEVOX terms of use for ずんだもん): "VOICEVOX:ずんだもん". Standard library only.
"""
import argparse
import hashlib
import json
import os
import sys
import urllib.parse
import urllib.request
import wave

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, HERE)
import overlay  # noqa: E402

VOICE_DIR = os.path.join(HERE, "voice")
CREDIT = "VOICEVOX:ずんだもん"
# words VOICEVOX reads wrongly: the text sent for synthesis (the sentence in index.json stays as it is)
READINGS = [
    ("弱強パンチ入替", "ジャクキョウパンチいれかえ"),
    ("強パンチ", "キョウパンチ"),
    ("弱パンチ", "ジャクパンチ"),
    ("ちょい遅延", "ちょいちえん"),
]


def restrict_labels():
    """The labels of the default restrictions (restrict.py), without needing MCPCon."""
    try:
        import restrict
        return [s["label_ja"] for s in restrict.load_steps(restrict.DEFAULT_PLAN, restrict.DEFAULT_PICKS)]
    except Exception as e:
        print("note: restrict.py could not be loaded (%s); using the built-in labels" % e)
        return ["ちょい遅延", "強パンチ気まぐれ", "弱強パンチ入替", "超遅延", "終了時間です"]


def reading(text):
    for a, b in READINGS:
        text = text.replace(a, b)
    return text


def synth(url, text, speaker, speed, rate):
    q = urllib.parse.urlencode({"text": text, "speaker": speaker})
    req = urllib.request.Request("%s/audio_query?%s" % (url, q), data=b"", method="POST")
    with urllib.request.urlopen(req, timeout=30) as r:
        query = json.loads(r.read().decode("utf-8"))
    query["speedScale"] = speed
    query["outputSamplingRate"] = rate
    query["outputStereo"] = False
    query["prePhonemeLength"] = 0.05
    query["postPhonemeLength"] = 0.1
    req = urllib.request.Request("%s/synthesis?speaker=%d" % (url, speaker), data=json.dumps(query).encode("utf-8"),
                                 headers={"Content-Type": "application/json"}, method="POST")
    with urllib.request.urlopen(req, timeout=60) as r:
        return r.read(), query.get("kana")


def main(argv=None):
    ap = argparse.ArgumentParser()
    ap.add_argument("--url", default="http://127.0.0.1:50021")
    ap.add_argument("--speaker", type=int, default=3, help="3 = ずんだもん (ノーマル)")
    ap.add_argument("--speed", type=float, default=1.1)
    ap.add_argument("--rate", type=int, default=16000, help="sampling rate of the WAV files")
    ap.add_argument("--force", action="store_true")
    a = ap.parse_args(argv)
    os.makedirs(VOICE_DIR, exist_ok=True)
    sentences = overlay.all_sentences(restrict_labels())
    index, made, total_sec = {}, 0, 0.0
    for text in sentences:
        key = json.dumps([text, reading(text), a.speaker, a.speed, a.rate], ensure_ascii=False)
        name = "v_%s.wav" % hashlib.sha1(key.encode("utf-8")).hexdigest()[:12]
        path = os.path.join(VOICE_DIR, name)
        if a.force or not os.path.exists(path):
            data, kana = synth(a.url, reading(text), a.speaker, a.speed, a.rate)
            with open(path, "wb") as f:
                f.write(data)
            made += 1
            print("made %s  %s  (%s)" % (name, text, kana))
        with wave.open(path) as w:
            total_sec += w.getnframes() / float(w.getframerate())
        index[text] = name
    keep = set(index.values())
    for f in os.listdir(VOICE_DIR):  # files of sentences that are no longer said
        if f.startswith("v_") and f.endswith(".wav") and f not in keep:
            os.remove(os.path.join(VOICE_DIR, f))
            print("removed", f)
    with open(os.path.join(VOICE_DIR, "index.json"), "w", encoding="utf-8") as f:
        json.dump({"credit": CREDIT, "speaker": a.speaker, "speed": a.speed, "rate": a.rate,
                   "made_by": "tools/make_voice.py", "sentences": index}, f, ensure_ascii=False, indent=1)
    size = sum(os.path.getsize(os.path.join(VOICE_DIR, n)) for n in keep)
    print("%d sentences, %d made now, %.1f s of speech, %.2f MB" % (len(index), made, total_sec, size / 1e6))
    return 0


if __name__ == "__main__":
    sys.exit(main())
