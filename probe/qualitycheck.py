import os, json
import numpy as np
from PIL import Image
import onnxruntime as ort
from transformers import AutoTokenizer, AutoImageProcessor

BASE = r"C:\Users\Tayod\Downloads\experiment\ShowFolderCreator-GitHub\SchoolPdfSorter\probe"
MODELS = os.path.join(BASE, "models")
I8 = os.path.join(MODELS, "int8")
REFS = os.path.join(BASE, "refs")

refs = json.load(open(os.path.join(REFS, "refs.json"), encoding="utf-8"))
tok = AutoTokenizer.from_pretrained(MODELS)
ip = AutoImageProcessor.from_pretrained(MODELS)

ts = ort.InferenceSession(os.path.join(I8, "model_quantized.onnx"), providers=["CPUExecutionProvider"])
vs = ort.InferenceSession(os.path.join(I8, "vision_encoder_quantized.onnx"), providers=["CPUExecutionProvider"])

EMPTY = np.zeros((0, 512), np.float32)

def run_text(ids, feats=None):
    f = feats if feats is not None else EMPTY
    return ts.run(None, {
        "input_ids": np.array([ids], np.int64),
        "attention_mask": np.ones((1, len(ids)), np.int64),
        "image_features": f, "video_features": EMPTY, "audio_features": EMPTY,
    })[1][0]

def cos(a, b):
    return float(np.dot(a, b) / (np.linalg.norm(a) * np.linalg.norm(b)))

print("== int8 vs q4 (text) ==")
for t in refs["text_cases"]:
    emb8 = run_text(t["ids"])
    emb4 = np.array(t["embedding"], np.float32)
    print(f"  {t['name']}: cos {cos(emb8, emb4):.5f}")

print("== int8 vs q4 (full image pipeline) ==")
img_embs = {}
for ic in refs["image_cases"]:
    img = Image.open(os.path.join(REFS, ic["png"]))
    out = ip(images=img, return_tensors="np")
    pv = np.asarray(out["pixel_values"]).astype(np.float32)
    if pv.max() > 1.001:
        pv = pv / 255.0
    key = "image_position_ids" if "image_position_ids" in out else "pixel_position_ids"
    pos = np.asarray(out[key]).astype(np.int64)
    feats = vs.run(None, {"pixel_values": pv, "pixel_position_ids": pos})[0].reshape(-1, 512)
    emb8 = run_text(ic["ids"], feats)
    emb4 = np.array(ic["embedding"], np.float32)
    print(f"  {ic['name']}: cos {cos(emb8, emb4):.5f}")
    img_embs[ic["name"]] = emb8

print("== int8 cross-modal score table (compare to q4 in refs.json) ==")
text_embs = {t["name"]: run_text(t["ids"]) for t in refs["text_cases"]}
q4_scores = refs["scores"]
for name, emb in img_embs.items():
    row = {k: round(cos(emb, v), 4) for k, v in text_embs.items()}
    print(f"  {name}: {row}")
    print(f"       q4: {q4_scores[name]}")
