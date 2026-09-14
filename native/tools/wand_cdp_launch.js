const http = require('http');

function requireNumericId(value, label) {
  if (!/^\d+$/.test(value || '')) throw new Error(`${label} must contain digits only`);
  return value;
}

function getJson(url) {
  return new Promise((resolve, reject) => {
    const request = http.get(url, (response) => {
      let body = '';
      response.setEncoding('utf8');
      response.on('data', (chunk) => {
        body += chunk;
        if (body.length > 1024 * 1024) request.destroy(new Error('CDP target response is too large'));
      });
      response.on('end', () => {
        try { resolve(JSON.parse(body)); }
        catch (error) { reject(error); }
      });
    });
    request.setTimeout(2500, () => request.destroy(new Error('Wand CDP target request timed out')));
    request.on('error', reject);
  });
}

async function main() {
  const titleId = requireNumericId(process.argv[2], 'titleId');
  const gameId = requireNumericId(process.argv[3], 'gameId');
  const targets = await getJson('http://127.0.0.1:9222/json/list');
  const target = targets.find((item) => item.type === 'page' && item.title === 'Wand' && /^ws:\/\/127\.0\.0\.1(?::\d+)?\//.test(item.webSocketDebuggerUrl || ''));
  if (!target) throw new Error('A local Wand CDP page was not found');

  const socket = new WebSocket(target.webSocketDebuggerUrl);
  const timeout = setTimeout(() => socket.close(), 5000);
  await new Promise((resolve, reject) => {
    socket.addEventListener('open', resolve, { once: true });
    socket.addEventListener('error', reject, { once: true });
  });

  let nextId = 1;
  const pending = new Map();
  socket.addEventListener('message', (event) => {
    const message = JSON.parse(event.data);
    const waiter = pending.get(message.id);
    if (!waiter) return;
    pending.delete(message.id);
    if (message.error) waiter.reject(new Error(JSON.stringify(message.error)));
    else waiter.resolve(message.result);
  });
  const send = (method, params) => new Promise((resolve, reject) => {
    const id = nextId++;
    pending.set(id, { resolve, reject });
    socket.send(JSON.stringify({ id, method, params }));
  });

  const nonce = `${Date.now()}-${process.pid}`;
  const hash = `#/app/titles/${titleId}?autoLaunch=true&gameId=${gameId}&trainerId=&launchNonce=${nonce}`;
  const expression = `location.hash=${JSON.stringify(hash)};location.href`;
  const evaluation = await send('Runtime.evaluate', { expression, returnByValue: true });
  clearTimeout(timeout);
  socket.close();
  console.log(JSON.stringify({ navigated: true, href: evaluation?.result?.value || '', titleId, gameId }));
}

main().catch((error) => {
  console.error(error && (error.stack || error.message) || String(error));
  process.exitCode = 1;
});
