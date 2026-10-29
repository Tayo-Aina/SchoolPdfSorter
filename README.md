# School PDF Sorter

A small offline Windows app that files school PDFs and PowerPoint files into one
folder per course.

It is deliberately strict: a file moves only when **exactly one** course code from
your course list is identified and no other course code appears in the checked
material. If no code is found, or if two or more different course codes are found,
the file does not move at all. Timetables and multi-course past-question packs are
always left alone. There is no `_Unsorted` folder and no cleanup sweep.

It never renames a file. A very strong name/title match on an untouched file can
be shown as a note (`looks like EIE 301 ...`), but that note is only a hint: it
never moves or renames anything.

Before scanning, you choose whether to look through PDFs only, PowerPoint slides
only, or both. PowerPoint support reads `.pptx` and `.ppsx`; older `.ppt` files are
ignored. PowerPoints make a run take longer, and a PowerPoint file made only of
pictures has no text to read, so it stays where it is.

## Run it

Double-click `run.cmd` from this folder. It prefers the portable,
self-contained build in `publish\` when it exists. Otherwise it falls back to
the Debug build and the local .NET 10 runtime.

The app is offline and CPU-only. It does not call the internet.

## Portable exe (no .NET required)

Run `publish.cmd` once. It creates one file:

```text
publish\SchoolPdfSorterApp.exe
```

This is a self-contained 64-bit Windows executable: copy it to any Windows 10
or 11 x64 PC and double-click it. No .NET runtime, model folder, or other DLLs
are needed. The first launch may take a few seconds while Windows unpacks the
bundled runtime.

If the app has not been built for development:

```bat
"%USERPROFILE%\.dotnet10\dotnet.exe" build SchoolPdfSorter.sln
```

## How it decides

For each PDF it looks, in order, at:

1. the file name
2. the name of the parent folder
3. the PDF metadata title
4. the biggest-font header line on the first pages
5. the text layer of pages 1-5
6. OCR of pages 1-5 (scans only, rendered at 200 DPI)

For a PowerPoint file the same order applies, but step 3 is the document title,
step 4 is the first line of slide 1, and step 5 is the text of slides 1-5. A
PowerPoint file has no OCR fallback: slides that are only pictures have no text to
read.

All available evidence is combined before deciding. A file moves only when every
checked level points to the same single course:

- exactly one listed course code, and no other course code anywhere -> move it
  into that course's folder
- two or more listed course codes -> leave it where it is
- one listed course code plus a code from another course -> leave it where it is
- no course code -> try OCR for a PDF, then leave it where it is

The file name and folder are checked first. A different course code in the file
name (for example `CSC 201` when your list has only `EIE 301`) makes the app leave
that file alone immediately, without opening it. A course code in the folder name
is not trusted on its own: that PDF is opened and checked too, because a timetable
or another course's paper can be sitting inside a course folder.

Timetables are recognized from their name or content (`timetable`, `time table`,
`exam schedule`, `lecture schedule`, `schedule`, ...). They are always left alone,
even when one of your course codes appears on them. A file named like a past-question pack
(`PQ`, `past questions`, `question bank`, `merged`, `combined`, `compilation`,
...) is opened even when its name contains one course code, so a second course
hidden on a later page is found.

Inside the PDF, the title, header and first five pages are checked for **any**
course-like code, including codes from other departments. That is what catches a
timetable that contains `EIE 301` plus `CSC 201`, `MTH 101`, and so on. Ordinary
page furniture such as `Page 12`, `Table 3`, `Room 301`, `ISO 9001` or `ISBN 978`
is ignored, as are lowercase prose (`the answer is 180`) and short OCR fragments
such as `AB 80`.

A file that reaches the end with no code, or with several codes, stays in its
original folder and is listed once at the end. Files with a very high-confidence
name/title match get a `looks like ...` note in that list. The note is not a move
and not a rename.

## Using the app

1. Paste the source folder (or drag it onto the window).
2. Give me your courses. You can type a list or paste your whole course structure:

   ```text
   ECON 301 Microeconomics
   GST 102 Communication in English
   CSC 201 Data Structures
   MTH 101 Elementary Mathematics I
   PHY 105 General Physics
   BIO 150 General Biology
   ```

   Or paste a course-structure table (a portal page you copied, an exported
   spreadsheet or the printed structure), or drop in the file that holds it. I read
   the course codes out of it. See [Course structure](#course-structure) below.

   You can also press **Auto-fill from file names**. It reads the course codes in
   the names of the files in your source folder and builds the list for you, so you
   do not have to type anything. Folders are then named by code only (`EIE 326`),
   because the full course title is not reliably in a file name. If your file names
   have no course codes, it says so instead of guessing; type the list or import
   your course structure.

3. Choose what to look through: **PDFs only**, **PowerPoint slides only**, or
   **both**. PowerPoints take longer, because every PowerPoint file is opened and
   its slides read; `.ppt` files are ignored.
4. Paste the target folder. Its parent folder must already exist.
5. Review the plan. Nothing is written to disk until you approve it.
6. Type `/undo` to move the last approved batch back.

Useful commands: `/start`, `/cancel`, `/courses`, `/subfolders`, `/help`.

Only the file types you chose are considered: `.pdf`, `.pptx` and `.ppsx`.
Scan subfolders is on by default; the target folder is excluded when it is inside
the source folder.

## Course structure

You do not have to type your course list. At step 2 you can hand me the whole
course structure and I turn it into the list:

- **Paste it.** Copy the structure table from the portal or a PDF and paste it in.
  Line breaks, semicolons, tabs, commas and pipe tables all work, and a code and its
  title may share one cell or sit in separate columns.
- **Drop the file.** Drag a `.pdf`, `.txt`, `.md`, `.csv` or `.rtf` onto the window,
  use **Browse...**, or copy the file in Explorer and press `Ctrl+V`. A PDF is read directly.
  A scanned PDF is read with the same offline text reader the sort uses,
  which keeps the table's rows together.
- **Type the list as before.** The plain path is untouched, so an ordinary course
  list still keeps its exact wording.

The parser only pulls a course from a cell that opens with a code, or from a cell
that still carries the row's own columns (`... 3 C ...` or `Course Code: EIE 311,
Title: ...`). Furniture stays out: totals rows, page numbers, dates, department
names, and near-codes like `Page 12`, `Office 2013`, `Level 100`, `ISO 9001`. You
confirm the extracted list before anything moves, and can choose to name folders by
code only (`EIE 326`).

A scanned structure is imperfect, so the app is careful about it:

- A department is corrected only when the scan reading could not be a real one.
  For example, `ElE314` becomes `EIE 314` and `py111` becomes `PHY 111`. Each fix
  is listed for you to check.
- Two plausible spellings are **never** merged: `CST 111` is not folded into `CIT`,
  even when one of them appears far more often. Instead a line like `GRC 324` is
  reported ("GEC is spelled that way 17 times here") and left exactly as read.
- Nothing is filed on a guess. A misread code only means that course's files stay
  where they are; it can never pull a file into the wrong folder.

Because a scanned structure can lose a course this way, after finding your files the
app also reads the **file names** in that folder and, when one or two dozen files all
say `GST 311` or `ICE 414` while your list lacks them, it shows that list and asks
whether to add them. Only file names are read at that point: no file is opened and
nothing inside a file is read. Adding a course does not loosen the rule. A file still
moves only when exactly one of your courses is found in it and no other course code
appears. Say no and those files stay where they are.

## Second and later runs

After the first run, later runs do less work on their own. There is no option to
turn on and no step to perform:

- When the target folder is the source folder (or sits inside it), folders that
  name exactly one of your courses are treated as already sorted and are not
  walked again. A folder that names two courses is still scanned.
- Files the app already checked and left in place are remembered, so they are
  not reopened and their pages are not read again. They still appear in the plan
  and in the final "left untouched" list, exactly as before.
- A file is rechecked when its size, last-write time, the course list, or the app
  build changes. New files of the type you chose are always checked. Switching
  between PDFs and PowerPoints keeps the remembered decisions for the other type.

The memory lives outside your folders, at
`%LocalAppData%\SchoolPdfSorter\checked-files.json`. It is written before the plan
is shown, so cancelling a plan still saves the work. Deleting that file is safe:
the next run checks everything again.

## Updates

The app is offline-first: it never needs the network, and a check is attempted at most
once a day. Offline, a 404, or a malformed feed means "no update". Nothing is
blocked or reported as an error.

- `/update` checks now and offers the release as a normal chat message.
- **Update now** downloads it, verifies the size and SHA-256, replaces the running exe
  and restarts. Windows allows renaming a running exe, so no installer or admin rights
  are involved. The replaced build is kept as `SchoolPdfSorterApp.previous.exe`.
- `/rollback` puts that previous build back and restarts.
- **Skip this version** remembers the choice; `/update` still installs it on request.

The feed is one static file (`latest.json` at the repository root), so hosting costs
nothing and a lookup is one small HTTPS request:

```json
{
  "version": "1.1.0",
  "url": "https://github.com/Tayo-Aina/SchoolPdfSorter/releases/download/v1.1.0/SchoolPdfSorterApp.exe",
  "size": 116654590,
  "sha256": "...",
  "notes": "Course-structure import"
}
```

To publish a release: bump `<Version>` in `src\SchoolPdfSorter.App\SchoolPdfSorter.App.csproj`,
run `release.cmd` (it publishes, hashes the exe and writes `latest.json`), upload the exe
to a GitHub release tagged `v<version>`, then commit and push `latest.json`.

Notes and limits:

- The SHA-256 protects against a broken or tampered download; it is not a signature. For
  public distribution, sign the exe with Authenticode and pin the certificate.
- Updates are full downloads (about 111 MB), not deltas.
- If the app lives in a read-only folder (Program Files), the swap fails and the app says
  so; keep it somewhere the user can write, as before.
- Turn checks off with `"Enabled": false` in `%LocalAppData%\SchoolPdfSorter\update.json`.

## Counting installs

Two different numbers:

- `tools/download-count.ps1` prints GitHub's download counts per release. Instant and
  free, but a download is not a person: one user can download several times.
- Distinct installs need the anonymous counter in `tools/usage-worker` (Cloudflare
  Workers, free tier). Deploy it, then either put its address in
  `%LocalAppData%\SchoolPdfSorter\usage.json` or replace `DefaultCounterUrl` in
  `UsageCounter.cs` and cut a release.

How the count works:

- A random 32-character id is created on first run and stored at
  `%LocalAppData%\SchoolPdfSorter\install-id`. It is not derived from the machine,
  the user or the folder being sorted.
- Once a day the app requests `<counter>?id=<id>&v=<version>`. Nothing else is sent.
- The worker keeps one "seen at least once" key per id, and a "seen today" key that
  expires after 48 hours. `/stats?token=...` returns `total` and `today`.
- If the counter URL is the example address, the app sends nothing. `"enabled": false`
  in `usage.json` turns it off for one machine.
- This is still telemetry, so it is documented here rather than hidden. The id is
  random, but the request does reach a third-party server.

`tools/UsageDashboard` is a small window that shows the two counter numbers plus GitHub
downloads, and records one row per day so a history builds up. Run
`tools\UsageDashboard\run.cmd`.

## Support section

The "Support us" button appears only when support details were sealed into the build.
Put them in `support.local.json` next to this README (it is gitignored):

```json
{
  "banks": [
    { "bank": "Providus Bank", "number": "0123456789" }
  ],
  "repo": "https://github.com/Tayo-Aina/SchoolPdfSorter"
}
```

`release.ps1` scrambles the text with a fixed XOR key, seals it with SHA-256, and
compiles the result into the app. Editing the payload breaks the seal and hides the
whole section, and the repository link is pinned in code. This is tamper-resistance,
not encryption: anyone who rebuilds from source can recover the text, so do not rely
on it to keep the numbers secret.

## Build and test

```bat
"%USERPROFILE%\.dotnet10\dotnet.exe" build SchoolPdfSorter.sln
"%USERPROFILE%\.dotnet10\dotnet.exe" test tests\SchoolPdfSorter.Tests --no-build
```

Strict-mode benchmark against the 38-file test corpus:

```bat
"%USERPROFILE%\.dotnet10\dotnet.exe" run --project tools\SchoolPdfSorter.Bench -- --corpus probe\testcorpus
```

Add `--no-unlisted-stop` or `--no-suggest` to compare the two optional behaviours.

Portable build:

```bat
publish.cmd
```

The application icon lives at `src\SchoolPdfSorter.App\icon.ico` and is generated by
`tools/icon`:

```bat
"%USERPROFILE%\.dotnet10\dotnet.exe" run --project tools\icon -- src\SchoolPdfSorter.App\icon.ico preview.png
```

Validation so far: on the 38-file test corpus, 35 PDFs moved, 0 to the wrong
course, and 3 stayed in place (a timetable and files with no code). It takes
about 5 seconds end-to-end, including OCR of 10 scanned pages.

A read-only run over a real 766-PDF folder moved 363 files to a course folder,
filed none under the wrong course, and left 403 in place. The run includes OCR of
1,357 pages in about 5 minutes on a CPU-only machine. The untouched files are
listed once at the end; multi-course documents, timetables and schedules stay
where they are.

A repeat read-only scan of that folder, with the incremental memory warm, reused
all 726 unchanged files and recognized the 3 course folders, opening none of
them again. New PDFs dropped into the source folder are still checked normally.

The same folder's 8-page scanned course structure (a portal print with no text
layer) was read with the built-in OCR into 112 courses. 18 of those codes needed a
department fix, 6 plausible-but-odd readings were reported instead of changed, and
every extracted code was usable by the matcher.

## Limitations

- Only the first 5 pages or the first 5 slides are searched. A code that appears
  only later will not be found, and the file stays put.
- PowerPoint reading is text-only. A PowerPoint file made of pictures, or one with
  text stored as an image, cannot be read and stays where it is. Legacy `.ppt`
  files are not read at all.
- A different course code in the file name is trusted over the content and
  skips that file. This is deliberate: it avoids reading and OCR-ing files that
  clearly name another course.
- A course code in the folder name is verified against the file's contents before
  anything moves. If the file cannot be opened, it stays put.
- The rule is intentionally strict. A normal course handout that merely mentions
  another course code in its first five pages or slides will also stay put. That is
  the tradeoff for never filing a timetable or multi-course pack under one course.
- **Auto-fill from file names** only reads file names. A course with no code in any
  file name will not be in the automatic list; those files stay where they are
  unless you add the course yourself or import your course structure.
- A `looks like ...` note is only a hint. It uses conservative word matching and
  OCR-confusion fixes in codes; it never moves or renames a file.
- Course folders are named exactly as the courses are typed.
- A folder directly under the target that names exactly one course is assumed to
  be sorted. If you keep unrelated files in a folder with a course name, they will
  not be scanned. Put them somewhere else before running, or move them out of the
  target folder.
- The incremental memory is keyed to the course list and app build. Changing
  either one makes the next run check everything again.
- The portable build is Windows x64 only. The single exe is large because it
  contains the .NET desktop runtime and the offline OCR engine.
