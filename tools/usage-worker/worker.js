// Anonymous install counter for School PDF Sorter.
//
// GET /ping?id=<32 hex>&v=<version>   counts one install per day
// GET /stats?token=<secret>           returns { total, today, date }
//
// The KV namespace is bound as USAGE. Set STATS_TOKEN with:
//   wrangler secret put STATS_TOKEN

const INSTALL_ID = /^[a-f0-9]{32}$/i;

export default {
  async fetch(request, env) {
    const url = new URL(request.url);

    if (url.pathname === '/ping') return ping(url, env);
    if (url.pathname === '/stats') return stats(url, env);

    return new Response('not found', { status: 404 });
  },
};

async function ping(url, env) {
  const id = url.searchParams.get('id') || '';

  if (!INSTALL_ID.test(id)) {
    return new Response('bad id', { status: 400 });
  }

  const day = new Date().toISOString().slice(0, 10);
  const dayKey = `day:${day}:${id}`;
  const allKey = `all:${id}`;

  const [seenToday, seenEver] = await Promise.all([
    env.USAGE.get(dayKey),
    env.USAGE.get(allKey),
  ]);

  const writes = [];

  if (!seenToday) {
    // Kept for 48 hours: enough for today's count, no long-term daily trail.
    writes.push(env.USAGE.put(dayKey, '1', { expirationTtl: 60 * 60 * 48 }));
  }

  if (!seenEver) {
    writes.push(env.USAGE.put(allKey, day));
  }

  await Promise.all(writes);

  return new Response('ok', { status: 200 });
}

async function stats(url, env) {
  const token = url.searchParams.get('token') || '';

  if (!env.STATS_TOKEN || token !== env.STATS_TOKEN) {
    return new Response('forbidden', { status: 403 });
  }

  const today = new Date().toISOString().slice(0, 10);
  const [total, daily] = await Promise.all([
    countKeys(env.USAGE, 'all:'),
    countKeys(env.USAGE, `day:${today}:`),
  ]);

  return new Response(
    JSON.stringify({
      total,
      today: daily,
      date: today,
      note: 'KV listing is eventually consistent, so very recent installs may lag.',
    }),
    { headers: { 'content-type': 'application/json' } },
  );
}

async function countKeys(kv, prefix) {
  let cursor;
  let count = 0;

  do {
    const page = await kv.list({ prefix, cursor });
    count += page.keys.length;
    cursor = page.list_complete ? undefined : page.cursor;
  } while (cursor);

  return count;
}
