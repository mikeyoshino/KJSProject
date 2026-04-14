import { AwsClient } from 'aws4fetch';
import { jwtVerify } from 'jose';

// Shared helper: sign + fetch a B2 object, with Cloudflare edge caching.
// rangeHeader is optional (for resumable downloads).
async function fetchFromB2(env, ctx, b2Key, rangeHeader = '') {
  const aws = new AwsClient({
    accessKeyId: env.B2_APPLICATION_KEY_ID,
    secretAccessKey: env.B2_APPLICATION_KEY,
    service: 's3',
    region: env.B2_REGION,
  });

  // Encode each path segment — special chars like [ ] must be percent-encoded for B2/S3
  const encodedKey = b2Key.split('/').map(seg => encodeURIComponent(seg)).join('/');
  const b2Url = `${env.B2_ENDPOINT}/${env.B2_BUCKET}/${encodedKey}`;

  // Cache key excludes auth tokens so multiple requests share one cached entry.
  const cacheKey = new Request(b2Url, {
    headers: rangeHeader ? { Range: rangeHeader } : {},
  });

  const cache = caches.default;
  let response = await cache.match(cacheKey);

  if (response) {
    console.log(`Cache Hit: ${b2Key}`);
    return response;
  }

  console.log(`Cache Miss: ${b2Key}. Fetching from B2...`);
  const signedRequest = await aws.sign(new Request(b2Url, { headers: cacheKey.headers }));
  response = await fetch(signedRequest);

  if (response.status === 200 || response.status === 206) {
    ctx.waitUntil(cache.put(cacheKey, response.clone()));
  }

  return response;
}

// Throttles a ReadableStream to a maximum bytes-per-second rate.
function throttleStream(readable, bytesPerSecond) {
  const { readable: out, writable } = new TransformStream();
  const writer = writable.getWriter();
  (async () => {
    const reader = readable.getReader();
    const startTime = Date.now();
    let bytesWritten = 0;
    try {
      while (true) {
        const { done, value } = await reader.read();
        if (done) break;
        await writer.write(value);
        bytesWritten += value.byteLength;
        const expectedMs = (bytesWritten / bytesPerSecond) * 1000;
        const actualMs = Date.now() - startTime;
        if (expectedMs > actualMs) {
          await new Promise(resolve => setTimeout(resolve, expectedMs - actualMs));
        }
      }
    } catch (e) {
      await writer.abort(e);
      return;
    }
    await writer.close();
  })();
  return out;
}

export default {
  async fetch(request, env, ctx) {
    const url = new URL(request.url);

    // ── Route 1: /download — JWT-protected file download ──────────────────────
    if (url.pathname === '/download') {
      const token = url.searchParams.get('token');
      const file  = url.searchParams.get('file');

      if (!token || !file) {
        return new Response('Missing token or file parameter', { status: 400 });
      }

      try {
        const secret = new TextEncoder().encode(env.JWT_SECRET);
        const { payload } = await jwtVerify(token, secret);

        if (payload.file !== file) {
          return new Response('Token not valid for this file', { status: 403 });
        }

        const b2Key = file.startsWith('/') ? file.slice(1) : file;
        const b2Response = await fetchFromB2(env, ctx, b2Key, request.headers.get('Range') || '');

        // Force browser to download the file rather than display it
        const filename = b2Key.split('/').pop();
        const headers = new Headers(b2Response.headers);
        headers.set('Content-Disposition', `attachment; filename="${filename}"`);

        return new Response(b2Response.body, { status: b2Response.status, headers });

      } catch (e) {
        console.error(e);
        return new Response('Unauthorized or expired token', { status: 401 });
      }
    }

    // ── Route 2: /public-download — short-lived JWT, throttled to 5 MB/s ─────
    if (url.pathname === '/public-download') {
      const token = url.searchParams.get('token');
      const file  = url.searchParams.get('file');

      if (!token || !file) {
        return new Response('Missing token or file parameter', { status: 400 });
      }

      try {
        const secret = new TextEncoder().encode(env.JWT_SECRET);
        const { payload } = await jwtVerify(token, secret);

        if (payload.file !== file) {
          return new Response('Token not valid for this file', { status: 403 });
        }

        const b2Key = file.startsWith('/') ? file.slice(1) : file;

        // Fetch directly from B2 — bypass edge cache for throttled public downloads
        const aws = new AwsClient({
          accessKeyId: env.B2_APPLICATION_KEY_ID,
          secretAccessKey: env.B2_APPLICATION_KEY,
          service: 's3',
          region: env.B2_REGION,
        });
        const encodedKey = b2Key.split('/').map(seg => encodeURIComponent(seg)).join('/');
        const b2Url = `${env.B2_ENDPOINT}/${env.B2_BUCKET}/${encodedKey}`;
        const rangeHeader = request.headers.get('Range');
        const reqHeaders = rangeHeader ? { Range: rangeHeader } : {};
        const signedReq = await aws.sign(new Request(b2Url, { headers: reqHeaders }));
        const b2Response = await fetch(signedReq);

        if (!b2Response.ok && b2Response.status !== 206) {
          return new Response('File not found', { status: 404 });
        }

        const filename = b2Key.split('/').pop();
        const throttled = throttleStream(b2Response.body, 5 * 1024 * 1024); // 5 MB/s

        const headers = new Headers();
        headers.set('Content-Type', b2Response.headers.get('Content-Type') || 'application/octet-stream');
        headers.set('Content-Disposition', `attachment; filename="${filename}"`);
        headers.set('Cache-Control', 'no-store');
        if (b2Response.headers.has('Content-Length')) {
          headers.set('Content-Length', b2Response.headers.get('Content-Length'));
        }

        return new Response(throttled, { status: b2Response.status, headers });

      } catch (e) {
        console.error(e);
        return new Response('Unauthorized or expired token', { status: 401 });
      }
    }

    // ── Route 3: /scandal69/*, /posts/*, /asianscandal_posts/*, /JGirls/* — public image proxy ────────────────────────────
    // Images uploaded by the scraper or Supabase live under these prefixes.
    // No auth required — these are public website images.
    if (url.pathname.startsWith('/scandal69/') || url.pathname.startsWith('/posts/') || url.pathname.startsWith('/asianscandal_posts/') || url.pathname.startsWith('/JGirls/')) {
      const b2Key = url.pathname.slice(1); // strip leading /
      const response = await fetchFromB2(env, ctx, b2Key);

      if (!response.ok) {
        return new Response('Not Found', { status: 404 });
      }

      // Return image with permissive CORS so the browser can load it cross-origin.
      const headers = new Headers(response.headers);
      headers.set('Access-Control-Allow-Origin', '*');
      headers.set('Cache-Control', 'public, max-age=31536000, immutable');

      return new Response(response.body, { status: response.status, headers });
    }

    return new Response('Not Found', { status: 404 });
  },
};
