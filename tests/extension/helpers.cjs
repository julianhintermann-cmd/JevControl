'use strict';
/*
 * Shared helpers for the extension tests: Playwright loading (local or global install),
 * a tiny static HTTP server for the fixtures, and a fake Kairo desktop app that speaks the
 * bridge framing (4-byte little-endian length + UTF-8 JSON) over TCP.
 */

const { execSync } = require('node:child_process');
const fs = require('node:fs');
const http = require('node:http');
const net = require('node:net');
const path = require('node:path');

const ROOT = path.resolve(__dirname, '..', '..');
const EXTENSION_DIR = path.join(ROOT, 'extension');
const CONTENT_JS = path.join(EXTENSION_DIR, 'content.js');
const FIXTURES_DIR = path.join(__dirname, 'fixtures');
const EXTENSION_ID = 'fjdcafkellelfdkneebdlmoggkhkilmh';

/** Resolves Playwright from node_modules, NODE_PATH or the global npm root. */
function loadPlaywright() {
  try {
    return require('playwright');
  } catch (_) {
    // fall through to the global install
  }
  const candidates = [];
  try {
    candidates.push(path.join(execSync('npm root -g', { encoding: 'utf8' }).trim(), 'playwright'));
  } catch (_) {
    // npm not available
  }
  candidates.push(path.join(path.dirname(process.execPath), '..', 'lib', 'node_modules', 'playwright'));
  for (const c of candidates) {
    if (fs.existsSync(c)) {
      return require(c);
    }
  }
  throw new Error('Playwright not found – install it globally (npm i -g playwright) or set NODE_PATH');
}

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json',
  '.png': 'image/png',
};

/** Serves `dir` on 127.0.0.1 (random port); HTML files may use the {{ALT_ORIGIN}} placeholder. */
function startStaticServer(dir) {
  const server = http.createServer((req, res) => {
    const urlPath = decodeURIComponent(new URL(req.url, 'http://x').pathname);
    const file = path.join(dir, path.normalize(urlPath).replace(/^([/\\])+/, ''));
    if (!file.startsWith(dir) || !fs.existsSync(file) || fs.statSync(file).isDirectory()) {
      res.writeHead(404, { 'content-type': 'text/plain' });
      res.end('Not found');
      return;
    }
    res.writeHead(200, { 'content-type': MIME[path.extname(file)] || 'application/octet-stream' });
    if (path.extname(file) === '.html') {
      // {{ALT_ORIGIN}} = same server under a different host name => cross-origin (OOPIF) frames.
      const { port } = server.address();
      res.end(fs.readFileSync(file, 'utf8').replaceAll('{{ALT_ORIGIN}}', `http://localhost:${port}`));
      return;
    }
    fs.createReadStream(file).pipe(res);
  });
  return new Promise((resolve) => {
    server.listen(0, '127.0.0.1', () => {
      const { port } = server.address();
      resolve({
        url: `http://127.0.0.1:${port}`,
        port,
        close: () => new Promise((r) => server.close(() => r())),
      });
    });
  });
}

function encodeFrame(obj) {
  const body = Buffer.from(JSON.stringify(obj), 'utf8');
  const header = Buffer.alloc(4);
  header.writeUInt32LE(body.length, 0);
  return Buffer.concat([header, body]);
}

/** Incremental decoder for length-prefixed JSON frames. */
function createFrameDecoder(onMessage) {
  let buffer = Buffer.alloc(0);
  return (chunk) => {
    buffer = Buffer.concat([buffer, chunk]);
    while (buffer.length >= 4) {
      const len = buffer.readUInt32LE(0);
      if (buffer.length < 4 + len) break;
      const json = buffer.subarray(4, 4 + len).toString('utf8');
      buffer = buffer.subarray(4 + len);
      onMessage(JSON.parse(json));
    }
  };
}

/**
 * Emulates the Kairo desktop app (BridgeServer). The stub native host connects to it and
 * relays frames unchanged, exactly like Kairo.BrowserHost.exe does with the named pipe.
 */
class FakeDesktop {
  constructor() {
    this.messages = [];
    this.connections = 0;
    this.socket = null;
    this.pending = new Map();
    this.waiters = [];
    this.nextId = 1;
    this.server = net.createServer((socket) => this._onConnection(socket));
  }

  listen() {
    return new Promise((resolve) => {
      this.server.listen(0, '127.0.0.1', () => resolve(this.server.address().port));
    });
  }

  _onConnection(socket) {
    this.connections += 1;
    this.socket = socket;
    const decode = createFrameDecoder((msg) => this._onMessage(msg));
    socket.on('data', decode);
    socket.on('error', () => {});
    socket.on('close', () => {
      if (this.socket === socket) this.socket = null;
    });
  }

  _onMessage(msg) {
    this.messages.push(msg);
    if (msg.type === 'response' && this.pending.has(msg.id)) {
      const { resolve, timer } = this.pending.get(msg.id);
      clearTimeout(timer);
      this.pending.delete(msg.id);
      resolve(msg);
    }
    for (const w of [...this.waiters]) {
      if (w.predicate(msg)) {
        this.waiters.splice(this.waiters.indexOf(w), 1);
        clearTimeout(w.timer);
        w.resolve(msg);
      }
    }
  }

  /** Resolves with the first message (already received or future) matching `predicate`. */
  waitFor(predicate, timeoutMs = 15000, description = 'message', { includePast = true } = {}) {
    if (includePast) {
      const found = this.messages.find(predicate);
      if (found) return Promise.resolve(found);
    }
    return new Promise((resolve, reject) => {
      const w = { predicate, resolve };
      w.timer = setTimeout(() => {
        this.waiters.splice(this.waiters.indexOf(w), 1);
        reject(new Error(`Timed out after ${timeoutMs} ms waiting for ${description}`));
      }, timeoutMs);
      this.waiters.push(w);
    });
  }

  /** Sends a request and resolves with the raw response message. */
  request(method, params = {}, timeoutMs = 30000) {
    if (!this.socket) {
      return Promise.reject(new Error('Extension is not connected'));
    }
    const id = `req-${this.nextId++}`;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`Request ${method} (${id}) timed out`));
      }, timeoutMs);
      this.pending.set(id, { resolve, timer });
      this.socket.write(encodeFrame({ type: 'request', id, method, params }));
    });
  }

  /** Like request(), but throws unless the response is ok and returns `result`. */
  async call(method, params = {}, timeoutMs = 30000) {
    const res = await this.request(method, params, timeoutMs);
    if (!res.ok) {
      const err = new Error(`${method} failed: ${res.error && res.error.code}: ${res.error && res.error.message}`);
      err.response = res;
      throw err;
    }
    return res.result;
  }

  dropConnection() {
    if (this.socket) this.socket.destroy();
    this.socket = null;
  }

  close() {
    this.dropConnection();
    return new Promise((resolve) => this.server.close(() => resolve()));
  }
}

module.exports = {
  CONTENT_JS,
  EXTENSION_DIR,
  EXTENSION_ID,
  FIXTURES_DIR,
  FakeDesktop,
  createFrameDecoder,
  encodeFrame,
  loadPlaywright,
  startStaticServer,
};
