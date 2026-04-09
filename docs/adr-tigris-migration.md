# ADR: Migrate Object Storage from Backblaze B2 to Tigris

**Status:** Proposed  
**Date:** 2026-04-08

---

## Context

The current infrastructure uses **Backblaze B2 (us-east-005, Virginia)** as object storage, served globally via a **Cloudflare Worker** with edge caching.

This works well for US/EU traffic. The problem:

- B2 has no cross-region replication
- B2 has no AP (Asia-Pacific) region bucket pairing with the US bucket
- On cache miss, every Asian user request travels: **Asian PoP → B2 Virginia → Asian PoP** — slow
- Cloudflare's edge cache only helps for repeat requests to the same PoP; first-hit latency is always high for Asian users

As Asian traffic grows this becomes a user experience problem for downloads.

---

## Decision Drivers

- Cost must remain low (current B2 cost: ~$0.006/GB storage, $0 egress via Cloudflare Bandwidth Alliance)
- Must not require uploading to multiple buckets at write time
- Must remain S3-compatible (minimal code changes)
- Must integrate with existing Cloudflare Worker without major rework

---

## Options Considered

### Option A: Keep B2 US + improve Cloudflare cache TTL (current)
- **Cost:** $0.006/GB — cheapest
- **Asian speed:** Fast only after first hit per PoP; slow on cache miss
- **Verdict:** Works if content is popular and repeat-accessed. Fails for unique/fresh downloads.

### Option B: B2 US + B2 AP (double upload)
- **Cost:** ~$0.012/GB (2x storage)
- **Asian speed:** Fast
- **Verdict:** Requires uploading to two buckets in all write paths. Operational complexity for marginal saving over Tigris.

### Option C: Cloudflare R2
- **Cost:** $0.015/GB storage, free egress
- **Asian speed:** Fast (globally distributed)
- **Verdict:** Good option but more expensive than Tigris for storage-heavy workloads.

### Option D: Tigris (chosen)
- **Cost:** ~$0.02/GB storage, free egress via Cloudflare
- **Asian speed:** Fast — lazy global replication (copies data to regions where it is actually accessed, on demand)
- **Verdict:** Best balance. Only pays for replication where Asian users actually exist. S3-compatible drop-in replacement.

---

## Decision

**Migrate to Tigris** when Asian traffic becomes a measurable problem.

Tigris is S3-compatible and integrates with Cloudflare Workers natively (R2-compatible binding), which means:
- Zero egress fees (same as current B2 setup)
- No double-upload at write time
- Automatic lazy replication to Singapore/Tokyo/etc as Asian users access content
- Minimal code changes

---

## Implementation

### Files to Change

#### `scraper_service/src/storage_b2.py`
Change boto3 client endpoint and credentials:

```python
# Before (B2)
s3 = boto3.client(
    "s3",
    endpoint_url="https://s3.us-east-005.backblazeb2.com",
    region_name="us-east-005",
    aws_access_key_id=B2_KEY_ID,
    aws_secret_access_key=B2_APP_KEY,
)

# After (Tigris)
s3 = boto3.client(
    "s3",
    endpoint_url="https://fly.storage.tigris.dev",
    region_name="auto",
    aws_access_key_id=TIGRIS_ACCESS_KEY,
    aws_secret_access_key=TIGRIS_SECRET_KEY,
)
```

#### `scraper_service/.env`
```env
TIGRIS_ACCESS_KEY=tid_xxxx
TIGRIS_SECRET_KEY=tsec_xxxx
TIGRIS_BUCKET=your-bucket-name
```

#### `cloudflare_b2_worker/wrangler.toml`
Use Tigris native R2-compatible binding (eliminates HTTP overhead + egress):
```toml
[[r2_buckets]]
binding = "BUCKET"
bucket_name = "your-tigris-bucket-name"
```

Then in worker replace fetch calls with:
```js
const object = await env.BUCKET.get(key);
```

### What Does NOT Change
- Upload logic in `asianscandal_download.py`, `asianscandal_rewrite.py`
- Supabase DB schema and B2 key paths (`posts/{id}/thumbnail.jpg`, etc.)
- Worker auth, caching, and rate-limiting logic

### Migration Steps
1. Create Tigris account and bucket at [tigris.dev](https://www.tigris.dev)
2. One-time copy existing B2 files: `rclone copy b2:your-bucket tigris:your-bucket`
3. Update `scraper_service/.env` with Tigris credentials
4. Update `storage_b2.py` client config
5. Update `cloudflare_b2_worker/wrangler.toml` to use R2 binding
6. Update worker `index.js` to use `env.BUCKET.get(key)` instead of B2 fetch
7. Deploy worker: `wrangler deploy`
8. Verify uploads and downloads work end-to-end
9. Decommission B2 bucket after confidence period

---

## Cost Comparison (at 1 TB stored)

| Setup | Storage/mo | Egress | Asian first-hit latency |
|---|---|---|---|
| B2 US + CF cache (current) | ~$6 | Free | High (cache miss) |
| B2 US + B2 AP (double upload) | ~$12 | Free | Low |
| Cloudflare R2 | ~$15 | Free | Low |
| **Tigris (proposed)** | **~$20** | **Free** | **Low (lazy replication)** |

Note: Tigris storage cost grows only for regions where content is actually accessed. If Asian traffic is low, actual cost stays close to the base US price.

---

## Consequences

**Positive:**
- Asian users get fast downloads without double-upload complexity
- No changes to upload code paths or DB schema
- Free egress maintained via Cloudflare integration
- Worker code simplifies (R2 binding vs HTTP fetch)

**Negative:**
- Storage cost increases from ~$0.006/GB to ~$0.02/GB
- New vendor dependency (Tigris / Fly.io infrastructure)
- One-time migration effort (rclone copy + code changes)
