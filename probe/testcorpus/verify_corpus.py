# -*- coding: utf-8 -*-
"""Standalone pypdf verification of the generated corpus (reads only manifest.json).

Run:  python verify_corpus.py

Asserts: every file has >= 1 page and > 20 KB; kind text/batch extracts > 200
chars; kind scan extracts < 50 chars (no text layer). Prints PASS/FAIL counts.
"""
import json
from pathlib import Path

from pypdf import PdfReader

ROOT = Path(__file__).resolve().parent


def main():
    entries = json.loads((ROOT / "manifest.json").read_text(encoding="utf-8"))
    checks = {"pages_ge_1": [0, 0], "bytes_gt_20k": [0, 0],
              "text_gt_200": [0, 0], "scan_text_lt_50": [0, 0]}
    failures = []

    def rec(key, cond, label):
        checks[key][0] += 1
        if cond:
            checks[key][1] += 1
        else:
            failures.append(label)

    for e in entries:
        p = ROOT / e["file"]
        if not p.exists():
            failures.append(f"missing file: {e['file']}")
            continue
        rdr = PdfReader(str(p))
        txt = rdr.pages[0].extract_text() or ""
        rec("pages_ge_1", len(rdr.pages) >= 1, f"pages: {e['file']}")
        rec("bytes_gt_20k", p.stat().st_size > 20 * 1024, f"bytes: {e['file']}")
        if e["kind"] in ("text", "batch"):
            rec("text_gt_200", len(txt) > 200, f"text>200: {e['file']}")
        if e["kind"] == "scan":
            rec("scan_text_lt_50", len(txt) < 50, f"scan text<50: {e['file']}")

    labels = {
        "pages_ge_1": "pages >= 1 (all files)",
        "bytes_gt_20k": "bytes > 20 KB (all files)",
        "text_gt_200": "extracted text > 200 chars (text+batch)",
        "scan_text_lt_50": "extracted text < 50 chars (scans)",
    }
    print(f"corpus: {ROOT} | files in manifest: {len(entries)}")
    for k in ("pages_ge_1", "bytes_gt_20k", "text_gt_200", "scan_text_lt_50"):
        tot, ok = checks[k]
        print(f"  {'PASS' if tot == ok else 'FAIL'}  {labels[k]}: {ok}/{tot}")
    if failures:
        print("failures:")
        for f in failures:
            print(" ", f)
        raise SystemExit(1)


if __name__ == "__main__":
    main()
