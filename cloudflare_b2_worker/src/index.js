import { AwsClient } from 'aws4fetch';
import { jwtVerify } from 'jose';

export default {
  async fetch(request, env, ctx) {
    const url = new URL(request.url);

    // Only handle /download
    if (url.pathname !== '/download') {
      return new Response('Not Found', { status: 404 });
    }

    const token = url.searchParams.get('token');
    const file = url.searchParams.get('file');

    if (!token || !file) {
      return new Response('Missing token or file parameter', { status: 400 });
    }

    try {
      // 1. Verify JWT
      const secret = new TextEncoder().encode(env.JWT_SECRET);
      
      // jwtVerify will throw on expiration or invalid signature
      const { payload } = await jwtVerify(token, secret);
      
      // Verify token is meant for this file
      if (payload.file !== file) {
        return new Response('Token not valid for this file', { status: 403 });
      }

      // 2. Initialize aws4fetch to sign request for Backblaze B2
      // B2 S3 API expects region us-west-004 (or the region your bucket is in)
      const aws = new AwsClient({
        accessKeyId: env.B2_APPLICATION_KEY_ID,
        secretAccessKey: env.B2_APPLICATION_KEY,
        service: 's3',
        region: env.B2_REGION
      });

      // 3. Initialize Cache
      // NOTE: caches.default only works on Custom Domains (e.g., dl.kjsproject.com)
      // It will NOT work on the default *.workers.dev address.
      const cache = caches.default;
      const cleanFile = file.startsWith('/') ? file.substring(1) : file;
      const b2Url = `${env.B2_ENDPOINT}/${env.B2_BUCKET}/${cleanFile}`;

      // We use the b2Url (without the token) as the Cache Key
      // so multiple users with different tokens can share the same cached file.
      const cacheKey = new Request(b2Url, {
        headers: new Headers({
          'Range': request.headers.get('Range') || ''
        })
      });

      let response = await cache.match(cacheKey);

      if (!response) {
        console.log(`Cache Miss: ${cleanFile}. Fetching from B2 origin...`);

        // 4. Initialize aws4fetch to sign request for Backblaze B2
        const aws = new AwsClient({
          accessKeyId: env.B2_APPLICATION_KEY_ID,
          secretAccessKey: env.B2_APPLICATION_KEY,
          service: 's3',
          region: env.B2_REGION
        });

        const b2Request = new Request(b2Url, {
          method: request.method,
          headers: cacheKey.headers
        });

        const signedRequest = await aws.sign(b2Request);
        response = await fetch(signedRequest);

        // 5. Store in cache for future users
        // Only cache successful or partial content responses
        if (response.status === 200 || response.status === 206) {
           // We clone the response because the body can only be read once
           ctx.waitUntil(cache.put(cacheKey, response.clone()));
        }
      } else {
        console.log(`Cache Hit: ${cleanFile}. Serving from Cloudflare Edge...`);
      }

      return response;

    } catch (e) {
      console.error(e);
      return new Response('Unauthorized or expired token', { status: 401 });
    }
  }
};
