# Usage dashboard

A small window for checking how many people actually use the app. It is a personal tool,
not part of the released app.

It shows:

- **Unique installs**: distinct ids the usage worker has seen.
- **Active today**: ids that ran the app today.
- **Downloads**: total GitHub release downloads. This is not unique; one person can
  download several times.
- **History**: one row per day, saved whenever you refresh, so a picture builds up over
  time even though the worker only returns today's number.

## Run it

Double-click `tools\UsageDashboard\publish\UsageDashboard.exe` once it is published, or run
`tools\UsageDashboard\run.cmd`, which uses that exe when present and otherwise starts from
source with the private SDK.

To build the self-contained exe:

```bat
"%USERPROFILE%\.dotnet10\dotnet.exe" publish tools\UsageDashboard\UsageDashboard.csproj ^
  -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true ^
  -p:EnableCompressionInSingleFile=true ^
  -o tools\UsageDashboard\publish
```

The published folder is gitignored. The window refreshes on open and every five minutes
after that.

## Settings

Paste the worker address (with or without `/ping`) and the `STATS_TOKEN` you set for the
worker, then press Save. Both are stored in
`%LocalAppData%\SchoolPdfSorter\dashboard.json` as plain text, so keep this machine's
account to yourself.

Without a worker address the window still shows GitHub downloads, and the counter values
stay blank with `counter: not set`.

## History

The worker keeps today's count only. The dashboard writes a snapshot each time it
refreshes and keeps the last 180 days, so leave it open occasionally or press Refresh to
record a day.
