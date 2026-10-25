import json
from transformers import AutoProcessor
from PIL import Image

proc = AutoProcessor.from_pretrained("google/embeddinggemma-2")
print("processor class:", type(proc).__name__)

# --- text path ---
tok = proc.tokenizer
text = "task: classification | query: ECON 301 - Microeconomics"
enc = tok(text)
print("text:", text)
print("ids:", enc["input_ids"][:40], "... total", len(enc["input_ids"]))
print("tokens:", tok.convert_ids_to_tokens(enc["input_ids"])[:40])

# --- image path ---
img = Image.new("RGB", (800, 600), "white")
out = proc(images=img)
print("image processor output keys:", list(out.keys()))
for k, v in out.items():
    shape = getattr(v, "shape", None)
    if shape is not None:
        print(f"  {k}: shape={tuple(shape)} dtype={getattr(v, 'dtype', '?')}")
    else:
        print(f"  {k}: {v}")
