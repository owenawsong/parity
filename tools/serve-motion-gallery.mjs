import { createReadStream } from 'node:fs';
import { stat } from 'node:fs/promises';
import { createServer } from 'node:http';
import { extname, isAbsolute, relative, resolve, sep } from 'node:path';
import { pipeline } from 'node:stream/promises';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../dist/motion-gallery/', import.meta.url));
const port = Number(process.argv[2] ?? 8787);
const types = new Map([
  ['.html', 'text/html; charset=utf-8'],
  ['.css', 'text/css; charset=utf-8'],
  ['.js', 'text/javascript; charset=utf-8'],
  ['.webm', 'video/webm'],
  ['.webp', 'image/webp'],
]);

await stat(resolve(root, 'index.html'));

createServer(async (request, response) => {
  if (request.method !== 'GET' && request.method !== 'HEAD') {
    response.writeHead(405, { Allow: 'GET, HEAD' }).end();
    return;
  }
  try {
    const pathname = decodeURIComponent(new URL(request.url, 'http://127.0.0.1').pathname);
    const path = resolve(root, pathname === '/' ? 'index.html' : `.${pathname}`);
    const inside = relative(root, path);
    const type = types.get(extname(path));
    if (inside === '..' || inside.startsWith(`..${sep}`) || isAbsolute(inside) || !type) {
      response.writeHead(404).end();
      return;
    }
    const { size } = await stat(path);
    const headers = {
      'Content-Type': type,
      'Accept-Ranges': 'bytes',
      'Cache-Control': 'no-cache',
      'X-Content-Type-Options': 'nosniff',
    };
    let start = 0;
    let end = size - 1;
    if (request.headers.range) {
      const range = /^bytes=(\d*)-(\d*)$/u.exec(request.headers.range);
      if (range?.[1]) {
        start = Number(range[1]);
        if (range[2]) end = Math.min(end, Number(range[2]));
      } else if (range?.[2]) {
        start = Math.max(0, size - Number(range[2]));
      } else {
        start = size;
      }
      if (start > end) {
        response.writeHead(416, { 'Content-Range': `bytes */${size}` }).end();
        return;
      }
      headers['Content-Range'] = `bytes ${start}-${end}/${size}`;
    }
    headers['Content-Length'] = end - start + 1;
    response.writeHead(request.headers.range ? 206 : 200, headers);
    if (request.method === 'HEAD' || size === 0) response.end();
    else await pipeline(createReadStream(path, { start, end }), response);
  } catch (error) {
    if (!response.headersSent) response.writeHead(error instanceof URIError ? 400 : 404).end();
  }
}).listen(port, '127.0.0.1', () => console.log(`Local gallery: http://localhost:${port}/`));
