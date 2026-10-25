import json

p = r"C:\Users\Tayod\Downloads\experiment\ShowFolderCreator-GitHub\SchoolPdfSorter\probe\models\tokenizer.json"
j = json.load(open(p, encoding="utf-8"))

m = j["model"]
vocab = m.get("vocab")
print("model type:", m["type"])
print("vocab size:", len(vocab))
print("byte_fallback:", m.get("byte_fallback"))
print("unk_id:", m.get("unk_id"))
print("pre_tokenizer:", json.dumps(j.get("pre_tokenizer"))[:400])
print("normalizer:", json.dumps(j.get("normalizer"))[:400])
print("decoder:", json.dumps(j.get("decoder"))[:200])
print("post_processor:", json.dumps(j.get("post_processor"))[:400])
at = j.get("added_tokens", [])
print("added_tokens count:", len(at))
for t in at[:10]:
    print("  ", t.get("id"), repr(t.get("content")), "special:", t.get("special"), "lstrip:", t.get("lstrip"), "rstrip:", t.get("rstrip"), "normalized:", t.get("normalized"))
print("  ...")
for t in at[-5:]:
    print("  ", t.get("id"), repr(t.get("content")))
