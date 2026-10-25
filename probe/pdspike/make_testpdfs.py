"""Generate synthetic school-like A4 test PDFs (1240x1754 @ ~150 DPI) for the PdSpike probe."""
from PIL import Image, ImageDraw, ImageFont

FONT = r"C:\Windows\Fonts\arial.ttf"
W, H = 1240, 1754  # A4 portrait at ~150 DPI
OUT = r"C:\Users\Tayod\Downloads\experiment\ShowFolderCreator-GitHub\SchoolPdfSorter\probe\pdspike\testpdfs\synth_school.pdf"

f_big = ImageFont.truetype(FONT, 56)
f_mid = ImageFont.truetype(FONT, 34)
f_body = ImageFont.truetype(FONT, 24)
f_small = ImageFont.truetype(FONT, 20)


def draw_lines(d, x, y, lines, font, gap):
    for ln in lines:
        d.text((x, y), ln, font=font, fill=(0, 0, 0))
        y += gap
    return y


def page1():
    img = Image.new("RGB", (W, H), (255, 255, 255))
    d = ImageDraw.Draw(img)
    y = 140
    d.text((90, y), "UNIVERSITY OF LAGOS", font=f_big, fill=(0, 0, 0)); y += 90
    d.text((90, y), "DEPARTMENT OF ECONOMICS", font=f_mid, fill=(0, 0, 0)); y += 70
    d.text((90, y), "ECON 301 MICROECONOMICS", font=f_mid, fill=(0, 0, 0)); y += 90
    d.text((90, y), "Week 6: Elasticity", font=f_mid, fill=(0, 0, 0)); y += 100
    body = [
        "1. Define price elasticity of demand and explain its determinants.",
        "2. Distinguish between point elasticity and arc elasticity.",
        "3. A 10 percent rise in price reduces quantity demanded by 4 percent.",
        "   Compute the price elasticity of demand and classify the good.",
        "4. Discuss the relationship between elasticity and total revenue.",
        "5. Explain income elasticity and cross-price elasticity with examples.",
        "6. Derive the elasticity of a linear demand curve at any point.",
        "7. Explain why perfectly elastic demand curves are horizontal.",
        "8. Outline two real-world applications of elasticity in pricing.",
    ]
    y = draw_lines(d, 90, y, body, f_body, 44)
    y += 40
    d.text((90, y), "Submit answers before Friday 5pm.", font=f_small, fill=(0, 0, 0))
    return img


def page2():
    img = Image.new("RGB", (W, H), (255, 255, 255))
    d = ImageDraw.Draw(img)
    y = 150
    d.text((90, y), "COURSE OUTLINE AND READING LIST", font=f_big, fill=(0, 0, 0)); y += 95
    d.text((90, y), "CSC 305: ALGORITHMS AND DATA STRUCTURES", font=f_mid, fill=(0, 0, 0)); y += 80
    body = [
        "Module 1: Complexity analysis, asymptotic notation.",
        "Module 2: Sorting algorithms and lower bounds.",
        "Module 3: Graph traversal and shortest paths.",
        "Module 4: Dynamic programming and greedy methods.",
        "Module 5: NP-completeness and reductions.",
        "",
        "Required text: Introduction to Algorithms, 4th edition.",
        "Assessment: two CA tests 30 percent, exam 70 percent.",
        "Tutorials: Wednesdays 2pm, Lab 3, Faculty of Science.",
    ]
    y = draw_lines(d, 90, y, body, f_body, 46)
    return img


def page3():
    img = Image.new("RGB", (W, H), (255, 255, 255))
    d = ImageDraw.Draw(img)
    lines = [
        "Questions for Revision",
        "",
        "Explain the concept of opportunity cost and give three examples",
        "from everyday decision making. Discuss how sunk costs should be",
        "treated in economic decision making and why they are excluded.",
        "Compare marginal analysis with average analysis and show, using",
        "a simple table, how a rational firm selects its output level.",
        "Describe the shape of the production possibility frontier when",
        "resources are not equally suited to both goods. Illustrate how",
        "technical progress shifts the frontier outward over time.",
        "Define consumer surplus and producer surplus, and explain how",
        "taxes create deadweight loss in a competitive market.",
    ]
    y = 160
    for ln in lines:
        f = f_mid if ln == "Questions for Revision" else f_body
        d.text((90, y), ln, font=f, fill=(0, 0, 0))
        y += 58 if ln else 30
    return img


pages = [page1(), page2(), page3()]
# Default PIL resolution is 72 dpi -> renders at 2583x3654 for these 150dpi images.
pages[0].save(OUT, save_all=True, append_images=pages[1:])
print("saved", OUT, "(default 72dpi page)")
# resolution=150 makes the page a true A4 (~595x842 pt) so 150dpi render is ~1240x1754.
OUT_A4 = OUT.replace("synth_school.pdf", "synth_school_a4.pdf")
pages[0].save(OUT_A4, save_all=True, append_images=pages[1:], resolution=150.0)
print("saved", OUT_A4, "(150dpi page)")
