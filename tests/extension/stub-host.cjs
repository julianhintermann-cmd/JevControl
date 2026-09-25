#!/usr/bin/env node
'use strict';
/*
 * Test stand-in for Kairo.BrowserHost.exe (the native messaging host "com.kairo.bridge").
 *
 * Like the real host it is a pure relay: frames (4-byte LE length + UTF-8 JSON) from Chrome
 * on stdin go unchanged to the desktop app, and frames from the desktop app go unchanged to
 * stdout. Instead of the named pipe it connects to the TCP port in KAIRO_STUB_PORT (the fake
 * desktop app started by the test; Chrome passes its environment on to native hosts).
 *
 * If the desktop app is not reachable it sends {"type":"status","status":"desktop_unavailable"}
 * and exits – exactly the behaviour the extension expects from the real host.
 */

const net = require('node:net');

function writeFrame(obj, callback) {
  const body = Buffer.from(JSON.stringify(obj), 'utf8');
  const header = Buffer.alloc(4);
  header.writeUInt32LE(body.length, 0);
  process.stdout.write(Buffer.concat([header, body]), callback);
}

function desktopUnavailable() {
  writeFrame({ type: 'status', status: 'desktop_unavailable' }, () => process.exit(0));
}

const port = Number(process.env.KAIRO_STUB_PORT);
if (!port) {
  desktopUnavailable();
} else {
  let connected = false;
  const socket = net.connect({ host: '127.0.0.1', port });
  socket.once('connect', () => {
    connected = true;
    process.stdin.pipe(socket);
    socket.pipe(process.stdout);
  });
  socket.on('error', () => {
    if (!connected) {
      desktopUnavailable();
    } else {
      process.exit(0);
    }
  });
  socket.on('close', () => {
    if (connected) process.exit(0);
  });
  process.stdin.on('end', () => {
    socket.end();
    process.exit(0);
  });
}
