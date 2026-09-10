const fs = require('node:fs');
const path = require('node:path');
const net = require('node:net');
const { spawn } = require('node:child_process');
const fixture = path.join(__dirname, 'evidence/web-regression');
(async () => {
  const listener = net.createServer();
  await new Promise(resolve => listener.listen(0, '127.0.0.1', resolve));
  const port = listener.address().port; await new Promise(resolve => listener.close(resolve));
  fs.copyFileSync(path.join(__dirname, '../public/app.js'), path.join(fixture, 'public/app.js'));
  fs.copyFileSync(path.join(__dirname, '../server.js'), path.join(fixture, 'server.js'));
  const out = fs.openSync(path.join(fixture, 'server.stdout.log'), 'w');
  const err = fs.openSync(path.join(fixture, 'server.stderr.log'), 'w');
  const server = spawn(process.execPath, ['server.js'], { cwd: fixture, env: { ...process.env, PORT: String(port) }, windowsHide: true, stdio: ['ignore', out, err] });
  try {
    let ready = false;
    for (let i = 0; i < 50; i++) { try { const r = await fetch('http://127.0.0.1:' + port + '/api/admin-config'); if (r.ok) { ready = true; break; } } catch {} await new Promise(resolve => setTimeout(resolve, 100)); }
    if (!ready) throw Error('Isolated regression server did not start.');
    const log = fs.openSync(path.join(fixture, 'test.log'), 'w');
    const test = spawn(process.execPath, ['tests/comprehensive-user-security.test.js'], { cwd: fixture, env: { ...process.env, TEST_SERVER_URL: 'http://127.0.0.1:' + port }, windowsHide: true, stdio: ['ignore', log, log] });
    const code = await new Promise((resolve, reject) => { test.on('error', reject); test.on('exit', resolve); });
    fs.closeSync(log);
    fs.writeFileSync(path.join(fixture, 'result.json'), JSON.stringify({ at: new Date().toISOString(), passed: code === 0, exitCode: code, server: 'http://127.0.0.1:' + port, fixture, productionMutated: false }, null, 2));
    console.log('Existing website regression suite exit=' + code + '; detailed output: ' + path.join(fixture, 'test.log'));
    process.exitCode = code;
  } finally { server.kill(); fs.closeSync(out); fs.closeSync(err); }
})().catch(error => { console.error(error); process.exitCode = 1; });
