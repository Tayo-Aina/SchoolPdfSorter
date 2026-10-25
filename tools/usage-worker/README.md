# Usage counter

A tiny Cloudflare Worker that counts **unique installs** of the app without collecting
personal data.

- The app sends one request a day, carrying a random 32-character install id and its
  version. The id is created on first run and never derived from the machine or the user.
- The worker stores `all:<id>` (seen at least once) and `day:<date>:<id>` (seen today,
  kept for 48 hours). Nothing else is stored, and no request logs are kept.
- Counting is off unless the app is built with a counter URL, so a plain checkout never
  phones home.

## Deploy

```bash
cd tools/usage-worker
npm install -g wrangler        # or use npx
wrangler login

wrangler kv namespace create USAGE
# copy the id into wrangler.toml

wrangler secret put STATS_TOKEN   # choose a long random value

wrangler deploy
```

The deploy prints an address like:

```text
https://schoolpdfsorter-usage.<account>.workers.dev
```

## Point the app at it

Either put it in `%LocalAppData%\SchoolPdfSorter\usage.json` on one machine:

```json
{
  "enabled": true,
  "counterUrl": "https://schoolpdfsorter-usage.<account>.workers.dev/ping"
}
```

Or, for a release that counts every user, replace `PlaceholderCounterUrl` in
`src/SchoolPdfSorter.Engine/UsageCounter.cs` before running `release.cmd`.

## Read the numbers

```text
https://schoolpdfsorter-usage.<account>.workers.dev/stats?token=<STATS_TOKEN>
```

```json
{ "total": 128, "today": 9, "date": "2026-10-25", "note": "KV listing is eventually consistent..." }
```

`total` is distinct installs ever seen. `today` is installs that ran the app today.
KV lists are eventually consistent, so a brand new install may take a minute to appear.

## Free tier

Workers and KV both have generous free limits (roughly 100k requests and 1k writes per
day). One ping per install per day is far below that until the app has tens of thousands
of daily users.

## Privacy

The id is random and not linked to a person, an account or a machine. Still, this is
telemetry: the app contacts a server once a day. Turn it off with `"enabled": false` in
`usage.json`, or build from source with the placeholder URL, which sends nothing.
