# -*- coding: utf-8 -*-
"""
make_corpus.py - reproducible generator for the "messy school folder" benchmark corpus.

Creates (relative to this file):
    messy/                    30 course PDFs (18 text + 12 scan), 5 GST batch files, 3 ambiguous
    messy/Downloads_Oct12/    the 5 GST 102 "portal export" batch PDFs
    manifest.json             ground truth for all 38 files

Run:  python make_corpus.py

Implementation notes / gotchas:
  * text/batch/ambiguous PDFs are reportlab files with a real selectable text layer.
    reportlab emits tiny files, so each is padded with valid PDF comment lines
    inserted between the trailer dict and the startxref keyword to land inside the
    required 30-400 KB window. Comments are legal between any two PDF tokens,
    are ignored by readers, and do not affect pypdf text extraction.
  * scan PDFs are Pillow RGB images (1240x1754, 150 dpi -> A4 page) with NO text
    layer: rotate(..., expand=False) keeps exactly 1240x1754 and clips corners to
    white fill, then gaussian blur, then a faint brightness gradient + noise in
    numpy. Pillow stores RGB PDF images with DCT (JPEG) encoding.
  * about half of the text PDFs carry a realistic title via canvas.setTitle(...);
    scans and batch exports never do (Pillow writes no /Title).
  * all randomness is seeded from the relative file path, so reruns are byte-stable.
"""
from __future__ import annotations

import io
import json
import os
import random
import shutil
import zlib
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont
from reportlab import rl_config

rl_config.invariant = 1  # fixed reportlab /ID + timestamps -> byte-stable reruns
from reportlab.lib.pagesizes import A4
from reportlab.lib.utils import ImageReader, simpleSplit
from reportlab.pdfbase.pdfmetrics import stringWidth
from reportlab.pdfgen import canvas as rl_canvas
from pypdf import PdfReader, PdfWriter

ROOT = Path(__file__).resolve().parent
MESSY = ROOT / "messy"
BATCH_DIR = MESSY / "Downloads_Oct12"

PAGE_W, PAGE_H = A4
MARGIN = 62
TEXT_PAD_MIN = (36_000, 46_000)   # comment-padding target range for reportlab files
SCAN_PAD_MIN = 32_000             # safety floor for scan files
HARD_MAX = 400_000
FIXED_PDF_DATE = "D:20260315120000Z"  # pinned so reruns are byte-stable

COURSE_FULL = {
    "ECON 301": "ECON 301 Microeconomics",
    "GST 102": "GST 102 Communication in English",
    "CSC 201": "CSC 201 Data Structures",
    "MTH 101": "MTH 101 Elementary Mathematics I",
    "PHY 105": "PHY 105 General Physics",
    "BIO 150": "BIO 150 General Biology",
}
COURSE_TITLE = {
    "ECON 301": "Microeconomics",
    "GST 102": "Communication in English",
    "CSC 201": "Data Structures",
    "MTH 101": "Elementary Mathematics I",
    "PHY 105": "General Physics",
    "BIO 150": "General Biology",
}

SENTENCES = {
    "ECON 301": [
        "Elasticity measures the responsiveness of quantity demanded to a change in price.",
        "The marginal rate of substitution declines as we move along an indifference curve.",
        "Total cost is the sum of fixed and variable costs at each level of output.",
        "A competitive firm produces at the point where marginal cost equals marginal revenue.",
        "Consumer surplus is the area below the demand curve and above the market price.",
        "The law of diminishing returns applies when extra units of a variable input are added.",
        "A price ceiling below equilibrium creates shortages in the market.",
        "Opportunity cost is the value of the next best alternative forgone.",
        "Demand curves shift when income, tastes, or the prices of related goods change.",
        "Monopoly output is lower and price is higher than under perfect competition.",
        "A production function shows the maximum output for each input combination.",
        "Tax incidence depends on the relative elasticities of supply and demand.",
    ],
    "GST 102": [
        "Communication is the transfer of a message from a sender to a receiver.",
        "Effective listening requires attention, patience, and honest feedback.",
        "Formal letters follow a standard structure with address, salutation, and body.",
        "Paraphrasing restates a passage in your own words without changing the meaning.",
        "The topic sentence introduces the main idea of a paragraph.",
        "Clarity and conciseness make writing easier to understand.",
        "Note taking helps the listener capture the main points of a lecture.",
        "Register refers to the level of formality in language use.",
        "An outline organises ideas before a long essay is written.",
        "Summary writing keeps only the essential points of a text.",
        "Non verbal cues include gestures, posture, and facial expression.",
        "A memorandum is a short, direct message used within a formal organisation.",
    ],
    "CSC 201": [
        "A linked list stores elements in nodes connected by pointers.",
        "Stacks follow the last in, first out principle.",
        "Queues process elements in first come, first served order.",
        "Binary search runs in logarithmic time on a sorted array.",
        "A binary tree has at most two children per node.",
        "Recursion solves a problem by calling a smaller version of itself.",
        "Hash tables map keys to buckets using a hash function.",
        "Insertion at the head of a singly linked list runs in constant time.",
        "Traversal visits every node of a tree exactly once.",
        "A stack overflow occurs when the call stack exceeds its limit.",
        "Circular queues reuse empty slots at the front of the array.",
        "Breadth first search uses a queue to explore nodes level by level.",
    ],
    "MTH 101": [
        "A quadratic equation can be written in the standard form ax squared plus bx plus c equals zero.",
        "The discriminant determines the nature of the roots of a quadratic equation.",
        "The sum of the interior angles of a triangle is 180 degrees.",
        "Indices follow the laws of exponents under multiplication and division.",
        "A straight line in the plane has the equation y equals mx plus c.",
        "Logarithms convert multiplication into addition.",
        "The binomial expansion gives the coefficients of powers of a sum.",
        "Sets can be combined using union and intersection operations.",
        "Partial fractions split a rational expression into simpler terms.",
        "The gradient of a curve at a point is found by differentiation.",
        "Simultaneous equations can be solved by substitution or by elimination.",
        "Surds are irrational roots written in simplified form.",
    ],
    "PHY 105": [
        "Newton's first law states that a body remains at rest or in uniform motion.",
        "Force equals mass times acceleration for a body of constant mass.",
        "The period of a simple pendulum depends on its length.",
        "Energy can be transformed from one form to another but not created or destroyed.",
        "Friction opposes relative motion between two surfaces in contact.",
        "Pressure in a fluid increases with depth.",
        "A vector quantity has both magnitude and direction.",
        "Ohm's law relates voltage, current, and resistance in a conductor.",
        "The acceleration due to gravity is about 9.8 metres per second squared.",
        "Work is done when a force moves an object along its direction.",
        "Momentum is conserved in an isolated system.",
        "Refraction bends light when it passes from one medium into another.",
    ],
    "BIO 150": [
        "The cell is the basic structural unit of living organisms.",
        "Mitosis produces two genetically identical daughter cells.",
        "Photosynthesis converts light energy into chemical energy.",
        "Deoxyribonucleic acid carries hereditary information from parents to offspring.",
        "Proteins are synthesised from amino acids at the ribosome.",
        "Enzymes lower the activation energy of biochemical reactions.",
        "The plasma membrane controls the movement of substances into the cell.",
        "Respiration releases energy from glucose in living cells.",
        "Punnett squares predict the outcome of simple genetic crosses.",
        "Bacteria are prokaryotes without a membrane bound nucleus.",
        "Ecosystems consist of communities of organisms and their physical environment.",
        "Osmosis is the movement of water across a semi permeable membrane.",
    ],
}

# 30 per-course files: 18 text, 12 scan (6 scans with a strong in-page header,
# 6 scans with the course code only in the middle of the page).
PLAN = [
    dict(rel="ECON301_Week6.pdf", code="ECON 301", kind="text", hint=True,
         title="Week 6 Lecture Notes \u2014 ECON 301", subtitle="Week 6 Lecture Notes",
         notes="clean name; metadata title"),
    dict(rel="ECON301_ProblemSet2.pdf", code="ECON 301", kind="scan", hint=True, strong=True,
         notes="scan with strong in-page header"),
    dict(rel="econ301 tutorial 4 (final).pdf", code="ECON 301", kind="text", hint=True,
         title=None, subtitle="Tutorial 4 \u2014 Elasticity Practice",
         notes="messy name with course code hint"),
    dict(rel="IMG_20260304_123456.pdf", code="ECON 301", kind="scan", hint=False, strong=False,
         notes="no name hint; code only mid-page (weak signal)"),
    dict(rel="document.pdf", code="ECON 301", kind="text", hint=False,
         title=None, subtitle=None, notes="no name hint; content-only identification"),

    dict(rel="GST102_ListeningSkills.pdf", code="GST 102", kind="text", hint=True,
         title="GST 102 \u2014 Listening Skills", subtitle="Listening Skills \u2014 Lecture Notes",
         notes="clean name; metadata title"),
    dict(rel="gst102 notes wk4.pdf", code="GST 102", kind="text", hint=True,
         title=None, subtitle="Week 4 Notes", notes="messy name with course code hint"),
    dict(rel="Communication in English handout.pdf", code="GST 102", kind="scan", hint=True, strong=True,
         notes="scan with strong in-page header; topic name hint"),
    dict(rel="Scan_0007.pdf", code="GST 102", kind="scan", hint=False, strong=False,
         notes="no name hint; code only mid-page (weak signal)"),
    dict(rel="GST102_Assignment1.pdf", code="GST 102", kind="text", hint=True,
         title="GST 102 Assignment 1 \u2014 Communication in English", subtitle="Assignment 1",
         notes="clean name; metadata title"),

    dict(rel="CSC201_LinkedLists.pdf", code="CSC 201", kind="text", hint=True,
         title="Week 8 Lecture Notes \u2014 CSC 201", subtitle="Week 8 Lecture Notes",
         notes="clean name; metadata title"),
    dict(rel="csc201 lecture7 stacks and queues.pdf", code="CSC 201", kind="text", hint=True,
         title="Lecture 7 \u2014 Stacks and Queues (CSC 201)", subtitle="Lecture 7 \u2014 Stacks and Queues",
         notes="messy name with course code hint"),
    dict(rel="CSC201_BinaryTrees.pdf", code="CSC 201", kind="scan", hint=True, strong=True,
         notes="scan with strong in-page header"),
    dict(rel="IMG_20260311_093045.pdf", code="CSC 201", kind="scan", hint=False, strong=False,
         notes="no name hint; code only mid-page (weak signal)"),
    dict(rel="2f4a9c1b8e77.pdf", code="CSC 201", kind="text", hint=False,
         title=None, subtitle=None, notes="no name hint; content-only identification"),

    dict(rel="MTH101 assignment FINAL v2 please print.pdf", code="MTH 101", kind="text", hint=True,
         title=None, subtitle="Assignment \u2014 Final Draft", notes="messy name with course code hint"),
    dict(rel="MTH101_Tutorial2.pdf", code="MTH 101", kind="text", hint=True,
         title="MTH 101 Tutorial 2 \u2014 Quadratic Equations", subtitle="Tutorial 2 \u2014 Quadratic Equations",
         notes="clean name; metadata title"),
    dict(rel="maths past questions 2024.pdf", code="MTH 101", kind="scan", hint=True, strong=True,
         notes="scan with strong in-page header; topic name hint"),
    dict(rel="Scan_0021.pdf", code="MTH 101", kind="scan", hint=False, strong=False,
         notes="no name hint; code only mid-page (weak signal)"),
    dict(rel="quadratic equations worksheet.pdf", code="MTH 101", kind="text", hint=True,
         title=None, subtitle="Worksheet \u2014 Quadratic Equations", notes="topic name hint only"),

    dict(rel="PHY105_Lab2_Report.pdf", code="PHY 105", kind="text", hint=True,
         title="PHY 105 Lab 2 \u2014 Simple Harmonic Motion", subtitle="Lab 2 \u2014 Simple Harmonic Motion",
         notes="clean name; metadata title"),
    dict(rel="phy105_lecture3 (1).pdf", code="PHY 105", kind="text", hint=True,
         title=None, subtitle="Lecture 3 \u2014 Newton's Laws", notes="messy name with course code hint"),
    dict(rel="IMG_20260313_171204.pdf", code="PHY 105", kind="scan", hint=False, strong=True,
         notes="no name hint; strong in-page header"),
    dict(rel="Scan_0034.pdf", code="PHY 105", kind="scan", hint=False, strong=False,
         notes="no name hint; code only mid-page (weak signal)"),
    dict(rel="newton laws of motion notes.pdf", code="PHY 105", kind="text", hint=True,
         title="PHY 105 \u2014 Newton's Laws of Motion", subtitle="Newton's Laws of Motion",
         notes="topic name hint; metadata title"),

    dict(rel="BIO150_CellStructure.pdf", code="BIO 150", kind="text", hint=True,
         title="BIO 150 \u2014 Cell Structure and Function", subtitle="Cell Structure and Function",
         notes="clean name; metadata title"),
    dict(rel="bio 150 practical manual.pdf", code="BIO 150", kind="scan", hint=True, strong=True,
         notes="scan with strong in-page header"),
    dict(rel="Scan_0042.pdf", code="BIO 150", kind="scan", hint=False, strong=False,
         notes="no name hint; code only mid-page (weak signal)"),
    dict(rel="ecosystems field trip notes.pdf", code="BIO 150", kind="text", hint=True,
         title=None, subtitle="Field Trip Notes", notes="topic name hint only"),
    dict(rel="document (2).pdf", code="BIO 150", kind="text", hint=False,
         title=None, subtitle=None, notes="no name hint; content-only identification"),
]

# All GST 102; "portal export" footer with a fake export timestamp per file.
BATCH_PLAN = [
    dict(rel="Downloads_Oct12/GST102_L3.pdf", lecture=3, minute=41),
    dict(rel="Downloads_Oct12/GST102_L4.pdf", lecture=4, minute=43),
    dict(rel="Downloads_Oct12/GST102_L4 (1).pdf", lecture=4, minute=44),
    dict(rel="Downloads_Oct12/GST102_L5.pdf", lecture=5, minute=46),
    dict(rel="Downloads_Oct12/GST102_L6.pdf", lecture=6, minute=49),
]

AMBIG_NOTES = {
    "academic_calendar.pdf": "timetable mentions all six courses roughly equally",
    "course_registration_form.pdf": "form fields; contains no course references",
    "fees_receipt.pdf": "receipt text; contains no course references",
}

# ---------------------------------------------------------------- helpers

def _seed(rel: str) -> int:
    return zlib.crc32(rel.replace("\\", "/").encode("utf-8")) & 0x7FFFFFFF


def pad_pdf(data: bytes, min_size: int, rnd: random.Random) -> bytes:
    """Grow a small reportlab PDF with valid PDF comment lines placed just before
    'startxref' (comments are legal between any two PDF tokens and are ignored
    by readers; nothing before the insertion point shifts, so offsets stay valid).
    """
    if len(data) >= min_size:
        return data
    idx = data.rfind(b"startxref")
    if idx == -1:
        return data
    need = min_size - len(data)
    pad = bytearray()
    while len(pad) < need:
        pad += b"%% pad %08x" % rnd.getrandbits(32)
        pad += b" " * 140 + b"\n"
    head = data[:idx]
    if not head.endswith(b"\n"):
        head += b"\n"
    return head + bytes(pad) + data[idx:]


def finalize_pdf(data: bytes, title, min_size: int, rnd: random.Random) -> bytes:
    """Normalize metadata via pypdf: remove the tool default /Title (reportlab
    writes 'untitled', Pillow writes the file stem), apply the requested title if
    any, pin CreationDate/ModDate so reruns are byte-stable, then pad to min_size.
    """
    reader = PdfReader(io.BytesIO(data))
    writer = PdfWriter(clone_from=reader)
    md = {}
    for key, value in (reader.metadata or {}).items():
        k = str(key)
        if k == "/Title":
            continue
        md[k] = str(value)
    md["/CreationDate"] = FIXED_PDF_DATE
    md["/ModDate"] = FIXED_PDF_DATE
    if title:
        md["/Title"] = title
    writer.metadata = md
    buf = io.BytesIO()
    writer.write(buf)
    return pad_pdf(buf.getvalue(), min_size, rnd)


def fit_font_size(text, font, maxw, start, floor=20):
    size = start
    while size > floor and stringWidth(text, font, size) > maxw:
        size -= 2
    return size


def render_blocks(c, blocks):
    """blocks: (font, size, gap_before, text) drawn top-down, wrapped with simpleSplit."""
    y = PAGE_H - 70.0
    usable = PAGE_W - 2 * MARGIN
    for font, size, gap, text in blocks:
        y -= gap
        c.setFont(font, size)
        for ln in simpleSplit(text, font, size, usable) or [""]:
            if y < 70:
                return y
            c.drawString(MARGIN, y, ln)
            y -= size * 1.35
    return y


def _write_reportlab(rel, title_meta, blocks, footer=None, pad_range=TEXT_PAD_MIN):
    out = MESSY / rel
    out.parent.mkdir(parents=True, exist_ok=True)
    rnd = random.Random(_seed(rel))
    buf = io.BytesIO()
    c = rl_canvas.Canvas(buf, pagesize=A4)
    if title_meta:
        c.setTitle(title_meta)
    render_blocks(c, blocks)
    if footer:
        c.setFont("Helvetica", 8)
        c.setFillGray(0.4)
        c.drawString(MARGIN, 42, footer)
        c.setFillGray(0.0)
    c.showPage()
    c.save()
    out.write_bytes(finalize_pdf(buf.getvalue(), title_meta, rnd.randint(*pad_range), rnd))
    return out


# ---------------------------------------------------------------- text / batch

def build_text_file(entry, body_n=None):
    rnd = random.Random(_seed(entry["rel"]))
    pool = SENTENCES[entry["code"]]
    n = body_n or rnd.randint(8, 12)
    body = rnd.sample(pool, n)
    header = f'{entry["code"]} \u2014 {COURSE_TITLE[entry["code"]]}'
    hsize = fit_font_size(header, "Helvetica-Bold", PAGE_W - 2 * MARGIN,
                          rnd.choice([28, 26, 24, 22, 20]))
    blocks = [("Helvetica-Bold", hsize, 0, header)]
    if entry.get("subtitle"):
        blocks.append(("Helvetica-Oblique", 12, 16, entry["subtitle"]))
    for s in body:
        blocks.append(("Helvetica", 11, 4, s))
    return _write_reportlab(entry["rel"], entry.get("title"), blocks)


def build_batch_file(b_rel, lecture, minute, rnd):
    rel = b_rel.replace("/", "\\")
    code = "GST 102"
    body = rnd.sample(SENTENCES[code], rnd.randint(8, 12))
    header = f'{code} \u2014 {COURSE_TITLE[code]}'
    blocks = [("Helvetica-Bold", 24, 0, header),
              ("Helvetica-Oblique", 12, 16, f"Lecture {lecture} \u2014 Course Notes")]
    for s in body:
        blocks.append(("Helvetica", 11, 4, s))
    footer = f"portal export 2026-10-12 09:{minute:02d}"
    return _write_reportlab(rel, None, blocks, footer=footer)


# ---------------------------------------------------------------- ambiguous

CAL_ROWS = [
    ("Mon", "08:00 - 10:00", "MTH 101 Elementary Mathematics I", "Lecture Hall A"),
    ("Mon", "10:00 - 12:00", "GST 102 Communication in English", "Lecture Hall B"),
    ("Tue", "08:00 - 10:00", "PHY 105 General Physics", "Physics Laboratory"),
    ("Tue", "12:00 - 14:00", "ECON 301 Microeconomics", "Lecture Hall C"),
    ("Wed", "08:00 - 10:00", "CSC 201 Data Structures", "Computer Lab 1"),
    ("Wed", "10:00 - 12:00", "BIO 150 General Biology", "Biology Laboratory"),
    ("Thu", "08:00 - 10:00", "GST 102 Communication in English", "Lecture Hall B"),
    ("Thu", "10:00 - 12:00", "MTH 101 Elementary Mathematics I", "Lecture Hall A"),
    ("Fri", "08:00 - 10:00", "CSC 201 Data Structures", "Computer Lab 2"),
    ("Fri", "10:00 - 12:00", "PHY 105 General Physics", "Physics Laboratory"),
    ("Mon", "14:00 - 16:00", "BIO 150 General Biology", "Biology Laboratory"),
    ("Tue", "14:00 - 16:00", "ECON 301 Microeconomics", "Lecture Hall C"),
    ("Wed", "14:00 - 16:00", "MTH 101 Elementary Mathematics I", "Lecture Hall A"),
    ("Thu", "12:00 - 14:00", "CSC 201 Data Structures", "Computer Lab 1"),
    ("Thu", "14:00 - 16:00", "BIO 150 General Biology", "Biology Laboratory"),
    ("Fri", "12:00 - 14:00", "GST 102 Communication in English", "Lecture Hall B"),
    ("Fri", "14:00 - 16:00", "ECON 301 Microeconomics", "Lecture Hall C"),
    ("Wed", "12:00 - 14:00", "PHY 105 General Physics", "Physics Laboratory"),
]


def build_calendar(rel):
    out = MESSY / rel
    rnd = random.Random(_seed(rel))
    buf = io.BytesIO()
    c = rl_canvas.Canvas(buf, pagesize=A4)
    c.setFont("Helvetica-Bold", 16)
    c.drawString(MARGIN, PAGE_H - 70, "FIRST SEMESTER TIMETABLE 2025/2026")
    c.setFont("Helvetica-Oblique", 10)
    c.drawString(MARGIN, PAGE_H - 88, "Faculty of Science \u2014 Undergraduate")
    y = PAGE_H - 118
    for day, slot, course, venue in CAL_ROWS:
        c.setFont("Helvetica", 10.5)
        c.drawString(MARGIN, y, day)
        c.drawString(MARGIN + 40, y, slot)
        c.drawString(MARGIN + 130, y, course)
        c.setFont("Helvetica", 9.5)
        c.drawString(MARGIN + 335, y, venue)
        y -= 17
    notes = ("The timetable is subject to change. Students should check the notice "
             "board for updates. Laboratory sessions begin in the second week of the "
             "semester. Report any clash to the Faculty Office within seven days.")
    c.setFont("Helvetica", 10)
    for ln in simpleSplit(notes, "Helvetica", 10, PAGE_W - 2 * MARGIN):
        y -= 16
        c.drawString(MARGIN, y, ln)
    c.showPage()
    c.save()
    out.write_bytes(finalize_pdf(buf.getvalue(), None, rnd.randint(*TEXT_PAD_MIN), rnd))
    return out


def build_registration_form(rel):
    out = MESSY / rel
    rnd = random.Random(_seed(rel))
    buf = io.BytesIO()
    c = rl_canvas.Canvas(buf, pagesize=A4)
    c.setFont("Helvetica-Bold", 15)
    c.drawString(MARGIN, PAGE_H - 70, "COURSE REGISTRATION FORM")
    c.setFont("Helvetica-Oblique", 10)
    c.drawString(MARGIN, PAGE_H - 88, "First Semester 2025/2026")
    fields = ["Surname", "Other Names", "Matriculation Number", "Faculty",
              "Department", "Level", "Session"]
    y = PAGE_H - 125
    for f in fields:
        c.setFont("Helvetica", 11)
        c.drawString(MARGIN, y, f + ": " + "_" * 40)
        y -= 22
    c.drawString(MARGIN, y, "Semester: [ ] First   [ ] Second")
    y -= 34
    c.setFont("Helvetica-Bold", 12)
    c.drawString(MARGIN, y, "COURSES REGISTERED THIS SEMESTER")
    y -= 22
    c.setFont("Helvetica-Bold", 10.5)
    c.drawString(MARGIN, y, "Course Code")
    c.drawString(MARGIN + 110, y, "Course Title")
    c.drawString(MARGIN + 400, y, "Units")
    y -= 18
    c.setFont("Helvetica", 10.5)
    for _ in range(8):
        c.drawString(MARGIN, y, "_" * 15)
        c.drawString(MARGIN + 110, y, "_" * 44)
        c.drawString(MARGIN + 400, y, "_" * 6)
        y -= 20
    notes = ("Every student must register the approved courses for the semester before "
             "the deadline. Forms must be signed by the course adviser and returned to "
             "the Faculty Office. Late registration attracts a penalty. Keep a copy of "
             "this form for your records.")
    c.setFont("Helvetica", 10)
    y -= 6
    for ln in simpleSplit(notes, "Helvetica", 10, PAGE_W - 2 * MARGIN):
        c.drawString(MARGIN, y, ln)
        y -= 15
    c.showPage()
    c.save()
    out.write_bytes(finalize_pdf(buf.getvalue(), "Course Registration Form",
                                 rnd.randint(*TEXT_PAD_MIN), rnd))
    return out


def build_receipt(rel):
    out = MESSY / rel
    rnd = random.Random(_seed(rel))
    buf = io.BytesIO()
    c = rl_canvas.Canvas(buf, pagesize=A4)
    c.setFont("Helvetica-Bold", 15)
    c.drawString(MARGIN, PAGE_H - 70, "UNIVERSITY BURSARY DEPARTMENT")
    c.setFont("Helvetica-Bold", 12)
    c.drawString(MARGIN, PAGE_H - 92, "SCHOOL FEES RECEIPT")
    lines = [
        ("Receipt No", "BU/2026/008432"),
        ("Date Issued", "2026-02-18"),
        ("Received From", "____________________________________________"),
        ("Matriculation Number", "2026/10492"),
        ("Session", "2025/2026 First Semester"),
        ("Purpose", "First semester school fees and charges"),
        ("Payment Method", "Bank transfer"),
        ("Bank Reference", "TRF-2026-02-18-00417"),
        ("Amount Paid", "NGN 87,500.00"),
        ("Amount in Words", "Eighty Seven Thousand Five Hundred Naira Only"),
        ("Status", "PAID"),
    ]
    y = PAGE_H - 130
    for k, v in lines:
        c.setFont("Helvetica-Bold", 11)
        c.drawString(MARGIN, y, k + ":")
        c.setFont("Helvetica", 11)
        c.drawString(MARGIN + 150, y, v)
        y -= 24
    notes = ("This receipt is computer generated and remains valid without a signature. "
             "Please keep it safe for clearance, hostel allocation, and examination "
             "registration. Queries should be directed to the Bursary within 30 days.")
    y -= 10
    c.setFont("Helvetica", 10)
    for ln in simpleSplit(notes, "Helvetica", 10, PAGE_W - 2 * MARGIN):
        c.drawString(MARGIN, y, ln)
        y -= 15
    c.showPage()
    c.save()
    out.write_bytes(finalize_pdf(buf.getvalue(), "Fees Receipt",
                                 rnd.randint(*TEXT_PAD_MIN), rnd))
    return out


# ---------------------------------------------------------------- scans

W, H = 1240, 1754
BG = (252, 251, 247)
INK = (30, 32, 36)
_FONT_DIR = Path(os.environ.get("WINDIR", r"C:\Windows")) / "Fonts"
_PROBE = ImageDraw.Draw(Image.new("RGB", (8, 8)))


def _load_font(bold, size):
    names = (["arialbd.ttf", "calibrib.ttf", "segoeuib.ttf", "verdana.ttf", "tahoma.ttf"]
             if bold else
             ["arial.ttf", "calibri.ttf", "segoeui.ttf", "verdana.ttf", "tahoma.ttf"])
    for n in names:
        p = _FONT_DIR / n
        if p.exists():
            try:
                return ImageFont.truetype(str(p), size)
            except OSError:
                pass
    for p in _FONT_DIR.glob("*.ttf"):
        try:
            return ImageFont.truetype(str(p), size)
        except OSError:
            continue
    return ImageFont.load_default()


def _fit_scan_font(text, bold, start, floor, maxw):
    size = start
    while size > floor:
        f = _load_font(bold, size)
        if _PROBE.textlength(text, font=f) <= maxw:
            return f
        size -= 2
    return _load_font(bold, floor)


def _draw_wrapped(draw, x, y, text, font, maxw, leading):
    line = ""
    for word in text.split():
        t = (line + " " + word).strip()
        if _PROBE.textlength(t, font=font) <= maxw or not line:
            line = t
        else:
            draw.text((x, y), line, font=font, fill=INK)
            y += leading
            line = word
    if line:
        draw.text((x, y), line, font=font, fill=INK)
        y += leading
    return y


def _embed_jpeg_fallback(img, out, quality):
    buf = io.BytesIO()
    img.save(buf, "JPEG", quality=quality, subsampling=0)
    buf.seek(0)
    c = rl_canvas.Canvas(str(out), pagesize=A4)
    c.drawImage(ImageReader(buf), 0, 0, width=A4[0], height=A4[1])
    c.showPage()
    c.save()


def _save_scan(img, out, rnd):
    size = 0
    for q in (88, 80, 72, 64, 56):
        try:
            img.save(out, "PDF", resolution=150, quality=q)
        except TypeError:
            img.save(out, "PDF", resolution=150)
            break
        size = out.stat().st_size
        if size <= HARD_MAX:
            break
    else:
        _embed_jpeg_fallback(img, out, 55)
    out.write_bytes(finalize_pdf(out.read_bytes(), None, SCAN_PAD_MIN, rnd))
    return out.stat().st_size


def build_scan(entry):
    rel = entry["rel"]
    out = MESSY / rel
    rnd = random.Random(_seed(rel))
    rng = np.random.default_rng(_seed(rel))
    code = entry["code"]
    img = Image.new("RGB", (W, H), BG)
    d = ImageDraw.Draw(img)
    margin = 80
    usable = W - 2 * margin
    body = rnd.sample(SENTENCES[code], rnd.randint(9, 12))
    f_body = _load_font(False, rnd.choice([30, 31, 32]))
    f_code = _load_font(True, 36)
    if entry["strong"]:
        header = f"{code} \u2014 {COURSE_TITLE[code]}"
        f_head = _fit_scan_font(header, True, 64, 48, usable)
        y = _draw_wrapped(d, margin, 130, header, f_head, usable, int(f_head.size * 1.3))
        y += 40
    else:
        pos = rnd.randrange(3, 6)
        body.insert(pos, f"{code} \u2014 {COURSE_TITLE[code]}")
        y = 200
    lead = 56
    for i, line in enumerate(body):
        f = f_code if (not entry["strong"] and i == pos) else f_body
        y = _draw_wrapped(d, margin, y, line, f, usable, lead)
    angle = rnd.uniform(0.4, 1.2) * rnd.choice([-1, 1])
    img = img.rotate(angle, resample=Image.BICUBIC, expand=False, fillcolor=(255, 255, 255))
    img = img.filter(ImageFilter.GaussianBlur(radius=rnd.uniform(0.5, 1.0)))
    arr = np.asarray(img, dtype=np.float32)
    gx = np.linspace(0.95, 1.03, W, dtype=np.float32)
    gy = np.linspace(1.0, 0.97, H, dtype=np.float32)
    arr *= gx[None, :, None]
    arr *= gy[:, None, None]
    arr += rng.normal(0.0, rnd.uniform(1.5, 2.5), arr.shape).astype(np.float32)
    np.clip(arr, 0.0, 255.0, out=arr)
    img = Image.fromarray(arr.astype(np.uint8), "RGB")
    _save_scan(img, out, rnd)
    return out


# ---------------------------------------------------------------- main / verify

def _rel(out: Path) -> str:
    return str(out.relative_to(ROOT))


def _pages(path: Path) -> int:
    return len(PdfReader(str(path)).pages)


def _entry(out, expected, kind, hint, notes):
    return dict(file=_rel(out), expected_course=expected, kind=kind,
                name_hint=hint, notes=notes,
                pages=_pages(out), bytes=out.stat().st_size)


def verify(entries):
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
        size = p.stat().st_size
        rec("pages_ge_1", len(rdr.pages) >= 1, f"pages: {e['file']}")
        rec("bytes_gt_20k", size > 20 * 1024, f"bytes: {e['file']}")
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
    print("\nverify summary (counts only):")
    for k in ("pages_ge_1", "bytes_gt_20k", "text_gt_200", "scan_text_lt_50"):
        tot, ok = checks[k]
        print(f"  {'PASS' if tot == ok else 'FAIL'}  {labels[k]}: {ok}/{tot}")
    return failures


def main():
    shutil.rmtree(MESSY, ignore_errors=True)
    MESSY.mkdir(parents=True, exist_ok=True)
    BATCH_DIR.mkdir(parents=True, exist_ok=True)
    entries = []

    for entry in PLAN:
        out = build_text_file(entry) if entry["kind"] == "text" else build_scan(entry)
        entries.append(_entry(out, COURSE_FULL[entry["code"]], entry["kind"],
                              entry["hint"], entry["notes"]))
        print(f"wrote {_rel(out):60s} {out.stat().st_size // 1024:4d} KB")

    for b in BATCH_PLAN:
        rnd = random.Random(_seed(b["rel"]))
        out = build_batch_file(b["rel"], b["lecture"], b["minute"], rnd)
        entries.append(_entry(out, COURSE_FULL["GST 102"], "batch", True,
                              f"portal export batch; footer 09:{b['minute']:02d}"))
        print(f"wrote {_rel(out):60s} {out.stat().st_size // 1024:4d} KB")

    for rel, builder in (("academic_calendar.pdf", build_calendar),
                         ("course_registration_form.pdf", build_registration_form),
                         ("fees_receipt.pdf", build_receipt)):
        out = builder(rel)
        entries.append(_entry(out, None, "ambiguous", False, AMBIG_NOTES[rel]))
        print(f"wrote {_rel(out):60s} {out.stat().st_size // 1024:4d} KB")

    (ROOT / "manifest.json").write_text(json.dumps(entries, indent=2), encoding="utf-8")

    by_kind = {}
    total_bytes = 0
    for e in entries:
        by_kind[e["kind"]] = by_kind.get(e["kind"], 0) + 1
        total_bytes += e["bytes"]
    print(f"\nmanifest: {ROOT / 'manifest.json'}")
    print(f"files: {len(entries)} | total {total_bytes / 1e6:.2f} MB | by kind: {by_kind}")

    failures = verify(entries)
    if failures:
        print("\nfailures:")
        for f in failures:
            print("  FAIL:", f)
        raise SystemExit(1)
    print("all checks passed")


if __name__ == "__main__":
    main()
