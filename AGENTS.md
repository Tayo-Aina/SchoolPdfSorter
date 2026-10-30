# AGENTS.md

Notes for anyone (human or agent) changing this app. Read this before touching the
matching rules, the icon, the updater or the release feed.

## What this is

`SchoolPdfSorter` is a self-contained, offline Windows WPF app that files school
documents into one folder per course. It is deliberately conservative: a file moves
only when exactly one listed course code is found and no other course code appears.
There is no cloud service, no Office install requirement and no image OCR for PowerPoint files.

- Solution: `SchoolPdfSorter.sln`
- Engine: `src/SchoolPdfSorter.Engine`
- WPF app and conversation flow: `src/SchoolPdfSorter.App`
- Tests: `tests/SchoolPdfSorter.Tests`
- Release scripts: `release.ps1`, `release.cmd`, `publish.cmd`

## Build and test

Use the repo's local .NET when `dotnet` is not on `PATH`:

```bat
"%USERPROFILE%\.dotnet10\dotnet.exe" build SchoolPdfSorter.sln
"%USERPROFILE%\.dotnet10\dotnet.exe" test tests\SchoolPdfSorter.Tests --no-build --no-restore
```

Do not add NuGet packages casually. PDF text uses PdfPig; OCR is the bundled
pure-C# NanoOCR. PowerPoint reading deliberately uses the framework only
(`System.IO.Compression` + `System.Xml.Linq`), so there is no Office interop and no
extra runtime to bundle. The single-file publish is already large.

## The rule that must not be weakened

`Sorter` combines evidence from the file name, parent folder, metadata title,
heading/first line, page/slide text and (PDF only) OCR. A move requires:

1. exactly one listed course code across all evidence, **and**
2. no other course-like code anywhere, even one that is not on the user's list.

Zero codes, two listed codes, a listed code plus a foreign code, or a timetable /
multi-course marker all mean "leave it exactly where it is". `StrictOptions` may
change how much extra work is done, never the decision rule. Tests in
`SorterTests.cs` assert the exact reasons; update them only together with a
deliberate behaviour change.

One deliberate exception: an unlisted code whose number matches a listed course and
whose letters are an anagram or one letter away from it is treated as that listed
course (`CEG 321` -> `GEC 321`). `CourseCodes.MatchVariant` returns a match only when
exactly one listed course fits; if two could fit, it returns null and the code stays
foreign. Do not broaden this into fuzzy matching of different numbers or unrelated
prefixes.

Timetable/multi-course detection is in `Sorter.IsTimetable` / `IsMultiCourse` and is
shared by filename and content checks. If a new phrase is added, add a test for both
a name hit and a content hit.

## Document types

- PDF: `.pdf`, first 5 pages (`PdfText.MaxPages`), text layer then OCR.
- PowerPoint: `.pptx`, `.ppsx` and `.pptm`, first 5 slides (`PptxText.MaxSlides`), text only.
  Slides are read from the Open XML zip; `presentation.xml` and its rels give the
  real slide order, with a numeric filename fallback. Speaker notes are not read.
- Legacy `.ppt` is intentionally unsupported and not read by the scanner. When
  PowerPoints are selected, `.ppt` files whose names carry a course code are listed
  in the run warning so the user can open them in PowerPoint and save them as
  `.pptx`; do not let them silently disappear.
- A PowerPoint file made only of pictures has no text. It is left alone with a "pictures" reason; never
  try to OCR a PowerPoint file.
- `FileSignals.PageCount` means pages for a PDF and slides for a PowerPoint file. Use
  `FileSignals.IsPresentation` to branch.

`Signals.ExtractSafe` dispatches by extension. Any new type belongs there and in
`PdfScanner.Scan(..., extensions)`; keep the scanner's extension match exact
(`Path.GetExtension`) rather than a wildcard, because Windows wildcard matching can
accept `.pdfx` for `*.pdf`.

## The conversation flow

`ChatSession.RunFlowAsync` is a linear script; every prompt is awaited. It is now
four steps:

1. source folder
2. course list (typed, imported, or found from the file names with
   **Find my courses from the file names**)
3. what to look through: PDFs only / PowerPoints only / both
4. target folder

Step 3 also posts a warning when PowerPoints are included (slower, picture-only
PowerPoint files cannot be read, legacy `.ppt` files are not read). After the scan,
`.ppt` files with a course code in the name are listed so the user can save them as
`.pptx`. If a step is added or reordered,
renumber every label, the help text and the greeting, and update README "Using the
app". The wording is for students: keep it plain and avoid technical jargon.

After the scan, `ReportScanNotes` explains what the run deliberately leaves alone:
Office lock files starting with `~$` (temporary files, not the real documents; the
user can delete them), folders whose names already match a course (left as they are;
folders are never renamed or moved), and Word/text/Excel/image files whose names
carry a course code (listed with advice to save them as PDF or PowerPoint). Keep
these notices honest and plain.

Step 2's **Find my courses from the file names** button calls
`CourseDiscovery.FromFileNames`, which reads only names. Do not turn it into a
content or OCR pass: a first-page scan was tried and produced dozens of fake codes
from ordinary words followed by numbers (`this`, `our`, `pmb`, `inc`, `Aina`, `ece`,
`for`, ...). If a code only appears inside a file, the normal leftover list names it
and the user can add it. Labels are still code-only (`EIE 326`) because titles are not
reliable. `CourseDiscovery` skips personal and administrative names (SIWES, SWEP,
internship, CV/resume, certificate, receipt, clearance, transcript, admission,
invoice, payment, birth, origin, medical/fitness, testimonial, statement of purpose,
logbook, defence) and level markers such as `300 level`; do not remove this filter, or
`Dara 400 siwes.pdf` becomes a fake course `DARA 400`.

The scanner is only given the extensions chosen in step 3. `ChatSession` also passes
those extensions to `ScanCache.Remember`, which keeps cached decisions for the other
file type instead of dropping them.

## Files and folders (non-obvious)

- `PdfScanner` treats a folder directly under the target that names exactly one
  course as already sorted. That only applies when the target is the source or lives
  inside it. A folder naming two courses is still scanned.
- A course code in a folder name is not enough to move an unreadable file; the file
  must be read and verified.
- A file whose name names another course is not opened (`StopOnUnlistedCode`).
- Nothing is ever renamed. `FileFiler` only creates the course folder and moves the
  file; name collisions are resolved by the filer, not by renaming the source.
- Set `SuggestNearMatches` output is a note only. It must never influence a move.

## Incremental cache

`ScanCache` lives at `%LocalAppData%\SchoolPdfSorter\checked-files.json`. It stores
only "left alone" decisions, keyed by source folder, course-list fingerprint, size
and last-write time. `ScanCache.Rules` is the Engine module version id, so **every
build invalidates the cache** and forces one full recheck. That is intentional.

The cache is written before the plan prompt, so cancelling still saves work. A
damaged or unwritable cache must never fail a run. Changing the cache format means
bumping `CurrentVersion`; do not migrate silently.

## Icon and header mark

The icon is generated, not hand-edited:

```bat
"%USERPROFILE%\.dotnet10\dotnet.exe" run --project tools\icon -- src\SchoolPdfSorter.App\icon.ico preview.png
```

Non-obvious details, learned the hard way:

- Frames are 32-bit BMPs with an AND mask. In an ICO, `biHeight` is **twice** the
  image height (XOR bitmap + AND mask). Writing only the image height makes Explorer
  crop/stretch the glyph.
- The AND mask must mark transparent pixels (1 = transparent). A zeroed mask makes
  shells that prefer the mask draw a black square.
- The ground is opaque white on purpose. A transparent background made the black
  page disappear in dark Explorer, so the glyph looked half-finished.
- Windows caches icons by path. If Explorer still shows an old icon, press `F5` or
  open a new window before rebuilding the icon cache. Rebuilding it (`iconcache_*.db`,
  `thumbcache_*.db`, restart Explorer) closes the user's Explorer windows.
- The in-window mark in `MainWindow.xaml` is hand-drawn and must stay visually the
  same as `icon.ico`. Change both together.

## Updates and releases

- Feed: `latest.json` at the repository root. `release.ps1` publishes, hashes and
  writes it; it does **not** upload the exe.
- Correct order: bump `<Version>` in `SchoolPdfSorter.App.csproj`, close the running
  app, run `release.ps1`, create the GitHub release `v<version>` and upload
  `publish\SchoolPdfSorterApp.exe`, then commit and push `latest.json`. If the feed
  is flipped before the asset exists, users get a broken update.
- Release asset URL pattern:
  `https://github.com/Tayo-Aina/SchoolPdfSorter/releases/download/v<version>/SchoolPdfSorterApp.exe`
- The updater verifies size and SHA-256, swaps by rename (Windows allows renaming a
  running exe), keeps `SchoolPdfSorterApp.previous.exe`, and `/rollback` restores it.
  Do not turn the update path into an installer.
- There is no `gh` CLI on this machine. Pushes use the stored Git Credential Manager
  credentials. Large pushes may need
  `-c http.version=HTTP/1.1 -c http.postBuffer=524288000`.
- The 111 MB release exe uploads slowly on this connection (about 0.25-0.5 MB/s,
  7-9 minutes). A reset/408 can happen near the end; retry the upload rather than
  assuming the asset is usable. An asset in `starter` state is incomplete and must
  be deleted before retrying.
- `release.ps1` seals support details from gitignored `support.local.json` into a
  generated `support.g.cs`. Never commit support numbers, and never commit the
  generated file. Tampering with the sealed payload hides the support section.
- The public repo has no license yet. Do not add dependencies with incompatible
  terms without deciding one.

## Usage counter

`tools/usage-worker` is a Cloudflare Worker backed by KV. The app sends only a random
local install id, version and date, at most once a day; it sends nothing when the
counter URL is the example placeholder. `STATS_TOKEN` is a Worker secret and the
dashboard asks for it; never commit it or bake it into the app. The dashboard is
`tools/UsageDashboard`.

## Tests worth knowing

- `SorterTests` — strict decision rule and reasons.
- `IncrementalTests` — scanner folder skipping, cache invalidation and extension
  filtering.
- `PptxTests` — minimal Open XML fixtures, slide order, slide text, PowerPoint moves
  and picture-only files.
- `CourseCodesTests`, `CourseStructureTests`, `PdfServicesTests`, `UpdateTests`,
  `UsageTests`, `FileFilerTests`.

If you add a prompt, add at least one test for the underlying engine behaviour. UI
wording itself is not unit-tested; verify the flow manually by launching
`publish\SchoolPdfSorterApp.exe` or the Debug build.
