// Minimal static file server for the Packetly web dashboard.
// Zero dependencies so `bun install` / `npm install` always succeed
// (Google AI Studio runs bun install at the repo root before starting
// the dev server). Honors PORT for hosted previews; serves web/.
import http from 'node:http';
import { promises as fs } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = path.dirname(fileURLToPath(import.meta.url));
const HOST = process.env.HOST || '0.0.0.0';
const PORT = Number(process.env.PORT) || 5173;

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.ico': 'image/x-icon',
  '.woff2': 'font/woff2',
  '.txt': 'text/plain; charset=utf-8',
};

const server = http.createServer(async (req, res) => {
  try {
    const url = new URL(req.url, 'http://x');
    let p = path.normalize(path.join(ROOT, decodeURIComponent(url.pathname)));
    if (!p.startsWith(ROOT)) {
      res.writeHead(403).end('forbidden');
      return;
    }
    let stat = await fs.stat(p).catch(() => null);
    if (stat && stat.isDirectory()) {
      p = path.join(p, 'index.html');
      stat = await fs.stat(p).catch(() => null);
    }
    if (!stat) {
      res.writeHead(404, { 'Content-Type': 'text/plain' }).end('not found');
      return;
    }
    const body = await fs.readFile(p);
    res.writeHead(200, {
      'Content-Type': MIME[path.extname(p).toLowerCase()] || 'application/octet-stream',
      'Content-Length': body.length,
      'Cache-Control': 'no-store',
    });
    res.end(body);
  } catch (err) {
    res.writeHead(500, { 'Content-Type': 'text/plain' }).end('server error');
  }
});

server.listen(PORT, HOST, () => {
  console.log(`Packetly web dashboard -> http://localhost:${PORT}`);
});
