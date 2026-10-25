import json, os, shutil, time, math
import numpy as np
from PIL import Image, ImageDraw, ImageFont
import onnxruntime as ort
from transformers import AutoTokenizer, AutoImageProcessor

TMP = r"C:\Users\Tayod\AppData\Local\Temp\opencode\eg2"
BASE = r"C:\Users\Tayod\Downloads\experiment\ShowFolderCreator-GitHub\SchoolPdfSorter\probe"
MODELS = os.path.join(BASE, "models")
REFS = os.path.join(BASE, "refs")
os.makedirs(REFS, exist_ok=True)

for src, dst in [("oc_processor_config.json", "processor_config.json"), ("oc_config.json", "config.json")]:
    s, d = os.path.join(TMP, src), os.path.join(MODELS, dst)
    if os.path.exists(s) and not os.path.exists(d):
        shutil.copyfile(s, d)
        print("copied", src, "->", dst)

print("== tokenizer ==")
tok = AutoTokenizer.from_pretrained(MODELS)
print("class:", type(tok).__name__)

def encode(text):
    return list(tok(text)["input_ids"])

print("'hello world':", encode("hello world")[:15])
print("'ECON 301':", encode("ECON 301")[:15])

print("== onnx sessions ==")
def make_sess(fname):
    return ort.InferenceSession(os.path.join(MODELS, fname), providers=["CPUExecutionProvider"])

ts = make_sess("model_q4.onnx")
for i in ts.get_inputs():
    print("  TEXT in :", i.name, i.shape, i.type)
for o in ts.get_outputs():
    print("  TEXT out:", o.name, o.shape, o.type)
vs = make_sess("vision_encoder_q4.onnx")
for i in vs.get_inputs():
    print("  VIS  in :", i.name, i.shape, i.type)
for o in vs.get_outputs():
    print("  VIS  out:", o.name, o.shape, o.type)

EMPTY = np.zeros((0, 512), dtype=np.float32)

def run_text(ids, image_features=None, verbose=False):
    ids2 = np.array([ids], dtype=np.int64)
    feeds = {
        "input_ids": ids2,
        "attention_mask": np.ones_like(ids2),
        "image_features": image_features if image_features is not None else EMPTY,
        "video_features": EMPTY,
        "audio_features": EMPTY,
    }
    t0 = time.perf_counter()
    outs = ts.run(None, feeds)
    dt = time.perf_counter() - t0
    if verbose:
        print(f"      text run: {dt*1000:.0f} ms  out {outs[1].shape}")
    return outs[1][0].astype(np.float32), dt

TEXTS = [
    ("tA", "task: classification | query: ECON 301 - Microeconomics"),
    ("tB", "task: classification | query: GST 102 - Communication in English"),
    ("tC", "task: search result | query: CSC 201 Data Structures - Lab 4 Linked Lists"),
    ("tD", "ECON 301 MICROECONOMICS\nWeek 6: Elasticity - Lecture Notes\nDr. Adeyemi"),
    ("tE", "GST 102 Communication in English Handout 3 Referencing and Academic Writing"),
    ("tF", "task: classification | query: CSC 209 \u2014 Computer Architecture (C++ & Assembly)"),
    ("tG", "GST 102: ''Referencing & Plagiarism''\n\tHandout \u2013 Dr. Ad\u00e9yemi"),
]
print("== text cases ==")
text_cases = []
text_embs = {}
for name, text in TEXTS:
    ids = encode(text)
    emb, dt = run_text(ids, verbose=True)
    text_embs[name] = emb
    print(f"  {name}: {len(ids)} ids, first {ids[:3]} last {ids[-2:]}")
    text_cases.append({"name": name, "text": text, "ids": ids, "embedding": emb.tolist()})

for _ in range(3):
    _, dt = run_text(encode(TEXTS[0][1]))
print(f"  warm text: {dt*1000:.0f} ms")

print("== images ==")
def make_img(sz, lines, sizes, names, font_dir=r"C:\Windows\Fonts"):
    img = Image.new("RGB", sz, "white")
    d = ImageDraw.Draw(img)
    y = 40
    for line, size in zip(lines, sizes):
        fontname = "arialbd.ttf" if size >= 34 else "arial.ttf"
        f = ImageFont.truetype(os.path.join(font_dir, fontname), size)
        d.text((60, y), line, fill="black", font=f)
        y += int(size * 1.45) + 12
    img.save(os.path.join(REFS, names + ".png"))
    return img

imgA = make_img((810, 1080), ["UNILAG", "ECON 301", "MICROECONOMICS", "Week 6: Elasticity", "Lecture Notes", "Dr. Adeyemi", "Department of Economics", "Elasticity measures the responsiveness", "of quantity demanded to price changes.", "Midpoint formula and determinants."], [46, 60, 42, 36, 28, 26, 24, 20, 20, 20], "imgA")
imgB = make_img((1280, 720), ["GST 102", "Communication in English", "Handout 3", "Referencing and Academic Writing", "Department of General Studies"], [62, 46, 36, 30, 26], "imgB")
imgC = make_img((600, 900), ["CSC 201", "Data Structures", "Lab 4: Linked Lists", "nodes, pointers, traversal"], [44, 38, 30, 22], "imgC")

try:
    ip = AutoImageProcessor.from_pretrained(MODELS)
    print("image processor:", type(ip).__name__)
except Exception as e:
    print("AutoImageProcessor failed:", repr(e)[:300])
    from transformers.models.gemma4.image_processing_pil_gemma4 import Gemma4ImageProcessorPil
    ip = Gemma4ImageProcessorPil()
    print("direct:", type(ip).__name__)

print("  patch_size", ip.patch_size, "max_soft_tokens", ip.max_soft_tokens, "pooling", ip.pooling_kernel_size, "rescale", ip.rescale_factor)

def target_size(h, w):
    target_px = 280 * 9 * 256
    factor = math.sqrt(target_px / (h * w))
    th = int(math.floor(factor * h / 48)) * 48
    tw = int(math.floor(factor * w / 48)) * 48
    return th, tw

BOI, IMGTOK, EOI = "<|image>", "<|image|>", "<image|>"

image_cases = []
for name, img in [("imgA", imgA), ("imgB", imgB), ("imgC", imgC)]:
    out = ip(images=img, return_tensors="np")
    pv = np.asarray(out["pixel_values"])
    key = "image_position_ids" if "image_position_ids" in out else "pixel_position_ids"
    pos = np.asarray(out[key])
    nsoft = np.asarray(out["num_soft_tokens_per_image"]).reshape(-1)
    N = int(nsoft[0])
    w0, h0 = img.size
    th, tw = target_size(h0, w0)
    gh, gw = th // 16, tw // 16
    P = gh * gw
    valid = int((pos.reshape(-1, 2)[:, 0] != -1).sum())
    print(f"  {name}: {w0}x{h0} -> {tw}x{th} grid {gh}x{gw} P={P} valid_rows={valid} nsoft={N}")
    print(f"      pv {pv.shape} {pv.dtype} range [{pv.min():.4f},{pv.max():.4f}]  pos {pos.shape} {pos.dtype}")

    pv32 = pv.astype(np.float32)
    if pv.dtype == np.uint8:
        pv32 = pv32 / 255.0
    pos64 = pos.astype(np.int64)
    pv32.tofile(os.path.join(REFS, name + "_pixel_values.f32.bin"))
    pos64.tofile(os.path.join(REFS, name + "_position_ids.i64.bin"))
    pv_u8 = np.clip(np.rint(pv32 * 255.0), 0, 255).astype(np.uint8)
    pv_u8.tofile(os.path.join(REFS, name + "_pixels_u8.bin"))
    print("      pos sample:", pos.reshape(-1, 2)[:4].tolist(), "row jump at", gh * gw and [gw, 2 * gw])

    # inverse-patchify the resized image for visual debugging
    try:
        flat = pv32.reshape(-1, 768)[:P]
        grid = flat.reshape(gh, gw, 16, 16, 3)
        im = grid.transpose(0, 2, 1, 3, 4).reshape(gh * 16, gw * 16, 3)
        lo, hi = float(im.min()), float(im.max())
        scale = 255.0 if hi <= 1.001 else 1.0
        Image.fromarray(np.clip(im * scale, 0, 255).astype(np.uint8)).save(os.path.join(REFS, name + "_resized.png"))
        print(f"      resized png saved ({gw*16}x{gh*16})")
    except Exception as e:
        print("      resize-debug skip:", repr(e)[:150])

    t0 = time.perf_counter()
    feats = vs.run(None, {"pixel_values": pv32, "pixel_position_ids": pos64})[0]
    dt = time.perf_counter() - t0
    feats = feats.reshape(-1, 512).astype(np.float32)
    feats.tofile(os.path.join(REFS, name + "_image_features.f32.bin"))
    print(f"      vision: {dt*1000:.0f} ms  image_features {feats.shape} (expect N={N})")

    template = BOI + IMGTOK * N + EOI
    ids = encode(template)
    print(f"      ids len {len(ids)}: {ids[:3]} ... {ids[-3:]}")
    emb, dt = run_text(ids, image_features=feats, verbose=True)

    image_cases.append({
        "name": name, "png": name + ".png", "target_h": th, "target_w": tw,
        "grid_h": gh, "grid_w": gw, "patches": P, "soft_tokens": N,
        "template": template, "ids": ids, "embedding": emb.tolist(),
    })

out = ip(images=imgA, return_tensors="np")
pv32 = np.asarray(out["pixel_values"]).astype(np.float32)
pos64 = np.asarray(out["image_position_ids" if "image_position_ids" in out else "pixel_position_ids"]).astype(np.int64)
if pv32.max() > 1.001:
    pv32 = pv32 / 255.0
for _ in range(2):
    t0 = time.perf_counter()
    vs.run(None, {"pixel_values": pv32, "pixel_position_ids": pos64})
    dt = time.perf_counter() - t0
print(f"  warm vision: {dt*1000:.0f} ms")

def cos(a, b):
    return float(np.dot(a, b) / (np.linalg.norm(a) * np.linalg.norm(b)))

print("== cross-modal scores ==")
scores = {}
for ic in image_cases:
    emb = np.array(ic["embedding"], dtype=np.float32)
    scores[ic["name"]] = {k: round(cos(emb, v), 4) for k, v in text_embs.items()}
    print(" ", ic["name"], scores[ic["name"]])

refs = {"text_cases": text_cases, "image_cases": image_cases, "scores": scores}
with open(os.path.join(REFS, "refs.json"), "w", encoding="utf-8") as f:
    json.dump(refs, f, indent=1)
print("saved", os.path.join(REFS, "refs.json"))
