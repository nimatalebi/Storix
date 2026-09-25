// Storix Telegram relay for Cloudflare Workers.
//
// Forwards Bot API calls (/bot<token>/<method>) and file downloads (/file/bot<token>/<path>) to the Telegram
// Bot API, for servers that cannot reach api.telegram.org directly. Nothing is stored.
//
// Settings (Workers > Settings > Variables):
//   RELAY_KEY     required secret; Storix sends it in the X-Storix-Relay-Key header
//   ALLOWED_BOTS  optional, comma-separated bot ids (the digits before ':' in the token)
//   UPSTREAM      optional, default https://api.telegram.org (e.g. https://tapi.bale.ai for Bale)

const RELAY_HEADER = 'X-Storix-Relay-Key';
const PATH = /^\/(?:file\/)?bot(\d+):[A-Za-z0-9_-]+\/[^?#]+$/;

function equal(a, b) {
  // Constant-time comparison of the relay key.
  const x = new TextEncoder().encode(a);
  const y = new TextEncoder().encode(b);
  let diff = x.length ^ y.length;
  for (let i = 0; i < Math.max(x.length, y.length); i++) {
    diff |= (x[i] ?? 0) ^ (y[i] ?? 0);
  }
  return diff === 0;
}

function reply(status, description) {
  return new Response(JSON.stringify({ ok: false, error_code: status, description }), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

export default {
  async fetch(request, env) {
    if (!env.RELAY_KEY) {
      return reply(500, 'RELAY_KEY is not configured on the relay.');
    }

    if (!equal(request.headers.get(RELAY_HEADER) ?? '', env.RELAY_KEY)) {
      return new Response('Forbidden', { status: 403 });
    }

    const url = new URL(request.url);
    const match = PATH.exec(url.pathname);
    if (!match || !['GET', 'POST'].includes(request.method)) {
      return reply(404, 'Not a Bot API path.');
    }

    const allowed = (env.ALLOWED_BOTS ?? '').split(',').map((s) => s.trim()).filter(Boolean);
    if (allowed.length > 0 && !allowed.includes(match[1])) {
      return reply(403, 'This bot is not allowed on the relay.');
    }

    const upstream = (env.UPSTREAM || 'https://api.telegram.org').replace(/\/+$/, '');
    const headers = new Headers(request.headers);
    headers.delete(RELAY_HEADER);
    headers.delete('host');

    // Bodies (up to the Workers request size limit, 100 MB on the free plan) are streamed, not buffered.
    return fetch(upstream + url.pathname + url.search, {
      method: request.method,
      headers,
      body: request.method === 'POST' ? request.body : undefined,
      duplex: 'half',
      redirect: 'follow',
    });
  },
};
