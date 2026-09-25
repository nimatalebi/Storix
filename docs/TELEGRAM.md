# Backups to Telegram (or Bale)

Storix can store backups as documents in a private Telegram channel or group. This is handy as a free,
off-site copy for small and medium backups. Always turn on **encryption** for these jobs: anyone in the chat
can download the files.

## 1. Create the bot and the chat

1. In Telegram, talk to **@BotFather**, send `/newbot` and copy the **bot token** (`123456789:AA...`).
2. Create a **private channel** (or a group) for the backups.
3. Add the bot as an **administrator** with the rights *Post messages*, *Delete messages* and *Pin messages*.
4. Find the **chat id**: post any message in the channel, then open
   `https://api.telegram.org/bot<token>/getUpdates` (or forward a message to @userinfobot). Channel ids look like
   `-1001234567890`.

## 2. Add the destination

In the job editor: **Destinations → Add… → Telegram**, then set:

| Setting | Value |
|---|---|
| Service | `Telegram` (or `Bale`, which offers the same Bot API) |
| Bot token | the token from BotFather (stored encrypted) |
| Chat id | e.g. `-1001234567890` |
| API base URL | empty for direct access, or your relay URL (see below) |
| Relay key | the relay's `RELAY_KEY` when you use a relay |
| Part size (MB) | `19` (see *Limits*) |

Click **Test connection**.

## How it works

- Every file is sent as one or more documents (`name.001`, `name.002`, … for larger files), without notification.
- Bots cannot read the history of a chat, so Storix keeps a small **catalog** (`storix-catalog.json`) with the
  list of files and their message ids. It is sent to the chat and **pinned**. Another computer with the same
  bot token and chat id finds it again, which means you can restore after losing the server. Do not unpin it.
  A copy of its location is also kept on the Storix machine.
- Retention deletes old backups from the catalog and deletes their messages.
- One Storix installation per chat: jobs on the same machine can share a chat, but two machines writing to the same
  chat can overwrite each other's catalog.

## Limits

- The official Bot API accepts uploads up to 50 MB, but **bots can only download files up to 20 MB**. That is why
  parts are 19 MB: every backup can also be restored through the bot.
- With your own [local Bot API server](https://github.com/tdlib/telegram-bot-api), point *API base URL* at it and
  raise the part size (up to 2000 MB).
- Telegram rate limits (HTTP 429) are respected automatically.

## When the server cannot reach Telegram: Cloudflare Worker relay

If `api.telegram.org` is blocked or unreachable from the server, run the relay in
[`tools/telegram-relay`](../tools/telegram-relay) on Cloudflare Workers (the free plan is enough: requests of up
to 100 MB, 100,000 requests a day). The relay only forwards Bot API calls, stores nothing and refuses requests
without the shared key.

**With the Cloudflare dashboard:**

1. *Workers & Pages → Create → Create Worker*, name it (e.g. `storix-relay`) and deploy.
2. *Edit code*, replace everything with the content of `tools/telegram-relay/worker.js` and deploy.
3. *Settings → Variables and secrets*: add a **secret** `RELAY_KEY` with a long random value. Optionally add
   `ALLOWED_BOTS` (your bot id, the digits before `:` in the token) and, for Bale,
   `UPSTREAM = https://tapi.bale.ai`.

**With the command line:**

```sh
cd tools/telegram-relay
npx wrangler deploy
npx wrangler secret put RELAY_KEY
```

In Storix set **API base URL** to the worker URL (e.g. `https://storix-relay.<you>.workers.dev`) and
**Relay key** to the same `RELAY_KEY`. The same two settings exist on Telegram/Bale **notification channels**
(*Settings → Chat and webhook channels*), so notifications can use the relay too.

Security notes: the bot token travels inside the URL to the relay (over HTTPS) and the relay sees the
(encrypted) backup data passing through, so deploy it in your own Cloudflare account. Storix never writes the
token to logs or error messages.
