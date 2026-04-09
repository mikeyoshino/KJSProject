# Cloudflare B2 Worker

A Cloudflare Worker reverse proxy for a **private** Backblaze B2 bucket. Handles two routes:

| Route | Auth | Purpose |
|---|---|---|
| `GET /scandal69/*` | None | Serve website images (thumbnails, content) |
| `GET /download?file=&token=` | JWT | Authenticated file downloads |

---

## How it works

The B2 bucket is private. The worker signs every request to B2 using AWS Signature V4 (`aws4fetch`) so credentials never leave Cloudflare. Responses are cached at the edge via `caches.default`.

---

## Deployment

### Step 1 — Create a Cloudflare account

1. Go to [dash.cloudflare.com](https://dash.cloudflare.com) and sign up / log in
2. In the left sidebar click **Workers & Pages**
3. Click **Create application** → **Create Worker** → **Deploy**
   - Cloudflare will ask you to pick a `*.workers.dev` subdomain (e.g. `yourname.workers.dev`) — this is free and only needs to be done once per account

### Step 2 — Install Wrangler (Cloudflare CLI)

Wrangler is the CLI used to deploy workers. Run once on your machine:

```bash
npm install -g wrangler
```

Then log in to your Cloudflare account:

```bash
wrangler login
```

A browser tab opens — approve the access request. You only need to do this once.

### Step 3 — Install worker dependencies

```bash
cd cloudflare_b2_worker
npm install
```

### Step 4 — Configure `wrangler.toml`

Update the vars to match your B2 bucket:

```toml
[vars]
B2_ENDPOINT = "https://s3.us-east-005.backblazeb2.com"   # your B2 region endpoint
B2_REGION   = "us-east-005"                               # your B2 region
B2_BUCKET   = "KJSProject"                                # your bucket name
```

> Do NOT put `JWT_SECRET` or B2 keys here — they go in as secrets in Step 6.

### Step 5 — Deploy

```bash
npx wrangler deploy
```

After deploy, Wrangler prints the worker URL:
```
https://b2-download-gatekeeper.<your-subdomain>.workers.dev
```

You can verify it exists in the Cloudflare dashboard under **Workers & Pages**.

### Step 6 — Set secrets

Secrets are encrypted by Cloudflare and never visible after being set. Run each command and paste the value when prompted:

```bash
npx wrangler secret put B2_APPLICATION_KEY_ID   # Backblaze key ID
npx wrangler secret put B2_APPLICATION_KEY       # Backblaze application key
npx wrangler secret put JWT_SECRET               # shared with KJSWeb appsettings.json
```

To verify secrets were saved: **Workers & Pages** → your worker → **Settings** → **Variables and Secrets**.

### Step 7 — Add a custom domain (recommended)

> `caches.default` (edge image caching) only works on Custom Domains, not `*.workers.dev`.

1. Your domain must be on Cloudflare (added under **Websites** in the dashboard)
2. Go to **Workers & Pages** → your worker → **Settings** → **Domains & Routes**
3. Click **Add** → **Custom Domain** → enter e.g. `cdn.scandal69.com`
4. Cloudflare automatically creates the DNS record and provisions SSL

### Step 8 — Update scraper `.env`

Point `B2_PUBLIC_BASE_URL` at the worker so uploaded images reference the proxy URL:

```env
B2_PUBLIC_BASE_URL=https://cdn.scandal69.com
# if no custom domain yet:
# B2_PUBLIC_BASE_URL=https://b2-download-gatekeeper.<your-subdomain>.workers.dev
```

### Step 9 — Update KJSWeb `appsettings.json`

Ensure `JWT_SECRET` matches the secret set in Step 6:

```json
"B2Worker": {
  "BaseUrl": "https://cdn.scandal69.com",
  "JwtSecret": "same-value-as-wrangler-secret"
}
```

---

## Routes

### `GET /scandal69/{filename}`

Proxies a scraper-uploaded image from B2. No authentication required.

**Example:**
```
GET https://cdn.scandal69.com/scandal69/a1b2c3d4e5f6....jpg
```

Response headers include:
- `Cache-Control: public, max-age=31536000, immutable`
- `Access-Control-Allow-Origin: *`

### `GET /download?file={path}&token={jwt}`

Proxies a file download from B2. Requires a valid short-lived JWT.

**Example:**
```
GET https://cdn.scandal69.com/download?file=posts/abc/file.zip&token=eyJ...
```

JWT payload must include:
- `file` — must match the `file` query param exactly
- `exp` — standard expiration claim

Supports `Range` header for resumable downloads (IDM, JDownloader).

---

## Re-deploy after code changes

```bash
cd cloudflare_b2_worker
npx wrangler deploy
```

Secrets are preserved across deploys — no need to re-set them.
