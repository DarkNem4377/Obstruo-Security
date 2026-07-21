# Obstruo — Blocklist Feed Format

Obstruo ships with a small built-in seed list. To block at real scale you point
it at a **feed**: an HTTPS URL that returns a JSON document of domains. Set the
URL in **Settings → Blocklist Feed** and click **Save URL & Sync Now**, or let
the daily auto-sync pull it (first run ~5 minutes after the service starts).

The feed is fetched by the service *through its own DNS proxy*, so the feed
host's own domain must resolve (i.e. not be blocked by your own list).

## Endpoint requirements

| Requirement | Value |
|---|---|
| Scheme | **HTTPS only** (`http://` is rejected) |
| Content | UTF-8 JSON (see below) |
| Max size | 64 MB |
| Max entries | 5,000,000 domains |

If the download fails, is too large, or isn't valid JSON, the sync is a **no-op**
— your current list stays exactly as it was (fail-safe, never half-applied).

## JSON shape

```json
{
  "versionName": "my-list-2026-07",
  "domains": [
    { "domain": "example-adult-site.com", "category": "Adult",   "wildcard": false },
    { "domain": "trackers.example.net",   "category": "Malware", "wildcard": true  },
    { "domain": "chat.example.org",        "category": "Chat"                       }
  ]
}
```

- **`versionName`** *(string, optional)* — free-text label recorded in sync history.
- **`domains`** *(array, required)* — the entries. An empty array is rejected.
- Property names are **case-insensitive** (`Domain`, `domain`, `DOMAIN` all work).

### Each domain entry

| Field | Type | Required | Meaning |
|---|---|---|---|
| `domain` | string | yes | The hostname, e.g. `example.com`. Must contain a dot and be a valid DNS name. Invalid entries are skipped, not fatal. |
| `category` | string | yes | Must match an existing category **name** (below). Unknown categories → the entry is skipped. |
| `wildcard` | bool | no (default `false`) | `false` blocks the domain **and all subdomains**. `true` blocks **subdomains only**, never the apex — stored internally as `*.domain`. |

> Note on `wildcard`: for a normal content block you usually want `false`.
> A non-wildcard entry for `example.com` already covers `www.example.com`,
> `m.example.com`, etc. Use `wildcard: true` only when you want to allow the apex
> but block everything beneath it.

### Valid category names

Category names must be one of the built-in categories (case-insensitive match):

`Adult`, `Chat`, `Games`, `AIAdult`, `Malware`, `Bypass`, `Custom`, `Paid`, `SexChat`

An entry whose `category` isn't in this list is silently skipped. (Obstruo does
not create new categories from a feed — toggle categories on/off in Settings.)

## How a sync is applied (reconciliation)

Every domain in the DB carries a **source**:

| Source | Set by | Touched by sync? |
|---|---|---|
| `charlie-beta4377` | built-in seed | **Never** |
| `custom` | you (Add-domain / whitelist) | **Never** |
| `sync` | a feed | **Fully reconciled** |

On each successful sync, the `sync`-sourced rows are made to exactly match the
feed: new domains added, changed categories updated, and **domains no longer in
the feed removed**. Your seed and custom entries are always preserved.

Obstruo hashes the feed body (SHA-256); if it's byte-identical to the last
successful sync, the sync is skipped as unchanged.

## Sourcing a list

Obstruo doesn't bundle a large list because a comprehensive, categorized,
maintained blocklist is licensed/curated *data*, not part of the app. Options:

- **Self-host** a JSON file in the format above (e.g. a static file on any HTTPS
  host) and convert an existing categorized source into it.
- **Point at a source you have the right to use.** If you adapt a public list,
  check its license, and transform it into this schema (category names must map
  to the list above).

Keep the feed URL private-ish if it reveals your policy, but note it contains no
secrets — it's just a domain list.
