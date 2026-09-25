// Tests for the relay: node --test tools/telegram-relay/
import { test } from 'node:test';
import assert from 'node:assert/strict';
import worker from './worker.js';

const env = { RELAY_KEY: 'secret', ALLOWED_BOTS: '', UPSTREAM: 'https://api.telegram.org' };
const token = '123456:ABC-def_ghi';

function withFetch(handler, body) {
  const original = globalThis.fetch;
  const calls = [];
  globalThis.fetch = async (url, init) => {
    calls.push({ url, init });
    return handler(url, init);
  };
  return body(calls).finally(() => { globalThis.fetch = original; });
}

const ok = () => new Response('{"ok":true,"result":{}}', { headers: { 'content-type': 'application/json' } });

test('forwards Bot API calls and removes the relay key', () => withFetch(ok, async (calls) => {
  const request = new Request(`https://relay.example/bot${token}/sendDocument?x=1`, {
    method: 'POST', headers: { 'X-Storix-Relay-Key': 'secret', 'content-type': 'text/plain' }, body: 'data',
  });
  const response = await worker.fetch(request, env);
  assert.equal(response.status, 200);
  assert.equal(calls.length, 1);
  assert.equal(calls[0].url, `https://api.telegram.org/bot${token}/sendDocument?x=1`);
  assert.equal(calls[0].init.headers.get('X-Storix-Relay-Key'), null);
  assert.equal(calls[0].init.method, 'POST');
}));

test('forwards file downloads', () => withFetch(ok, async (calls) => {
  const response = await worker.fetch(new Request(`https://relay.example/file/bot${token}/documents/file_1.bin`, { headers: { 'X-Storix-Relay-Key': 'secret' } }), env);
  assert.equal(response.status, 200);
  assert.equal(calls[0].url, `https://api.telegram.org/file/bot${token}/documents/file_1.bin`);
  assert.equal(calls[0].init.body, undefined);
}));

test('rejects a missing or wrong key', () => withFetch(ok, async (calls) => {
  for (const key of [undefined, 'wrong', 'secre']) {
    const headers = key ? { 'X-Storix-Relay-Key': key } : {};
    const response = await worker.fetch(new Request(`https://relay.example/bot${token}/getMe`, { headers }), env);
    assert.equal(response.status, 403);
  }
  assert.equal(calls.length, 0);
}));

test('only relays Bot API paths and allowed bots', () => withFetch(ok, async (calls) => {
  const headers = { 'X-Storix-Relay-Key': 'secret' };
  assert.equal((await worker.fetch(new Request('https://relay.example/https://evil.example/', { headers }), env)).status, 404);
  assert.equal((await worker.fetch(new Request(`https://relay.example/bot${token}/getMe`, { headers, method: 'DELETE' }), env)).status, 404);
  const limited = { ...env, ALLOWED_BOTS: '999' };
  assert.equal((await worker.fetch(new Request(`https://relay.example/bot${token}/getMe`, { headers }), limited)).status, 403);
  assert.equal(calls.length, 0);
}));

test('refuses to run without a configured key', () => withFetch(ok, async () => {
  const response = await worker.fetch(new Request(`https://relay.example/bot${token}/getMe`), { RELAY_KEY: '' });
  assert.equal(response.status, 500);
}));
