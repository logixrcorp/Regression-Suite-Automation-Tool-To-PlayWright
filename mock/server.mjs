// Serves one mock D365 page for every route, so the generated spec's
// menu-item deep links resolve without a real environment.
import { createServer } from 'node:http';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const html = readFileSync(join(here, 'd365-mock.html'), 'utf8');

createServer((_req, res) => {
  res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
  res.end(html);
}).listen(3999, '127.0.0.1', () => console.log('mock D365 on http://127.0.0.1:3999'));
