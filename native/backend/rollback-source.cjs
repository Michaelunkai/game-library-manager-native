// Default: validate a private rollback rehearsal. --apply restores the recorded local source.
// This does not deploy, mutate provider data, or touch files outside the exact receipt.
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const root = path.resolve(__dirname, '../..');
const receipt = JSON.parse(fs.readFileSync(path.join(__dirname, '../evidence/maintained-source.json'), 'utf8').replace(/^\uFEFF/, ''));
const hash = data => crypto.createHash('sha256').update(data).digest('hex');
const resolve = (base, name) => {
  const file = path.resolve(base, name);
  if (!file.startsWith(path.resolve(base) + path.sep)) throw Error('Receipt path escapes its root');
  return file;
};
if (path.resolve(receipt.sourceRoot) !== root) throw Error('Receipt targets another checkout');
for (const entry of receipt.files) {
  if (hash(fs.readFileSync(resolve(root, entry.path))) !== entry.afterSha256) throw Error('Source changed after preparation: ' + entry.path);
  if (entry.existed && hash(fs.readFileSync(resolve(receipt.backupRoot, entry.path))) !== entry.beforeSha256) throw Error('Backup checksum mismatch: ' + entry.path);
}
const rehearsal = path.join(receipt.backupRoot, 'rollback-rehearsal-' + Date.now());
for (const entry of receipt.files) {
  const file = resolve(rehearsal, entry.path);
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.copyFileSync(resolve(root, entry.path), file);
  if (entry.existed) fs.copyFileSync(resolve(receipt.backupRoot, entry.path), file);
  else fs.unlinkSync(file);
  if (entry.existed ? hash(fs.readFileSync(file)) !== entry.beforeSha256 : fs.existsSync(file)) throw Error('Rollback rehearsal failed: ' + entry.path);
}
const apply = process.argv.includes('--apply');
if (apply) {
  // Preserve the candidate privately before replacing any source file.
  const candidate = path.join(receipt.backupRoot, 'candidate-before-rollback-' + Date.now());
  for (const entry of receipt.files) {
    const backup = resolve(candidate, entry.path);
    fs.mkdirSync(path.dirname(backup), { recursive: true });
    fs.copyFileSync(resolve(root, entry.path), backup);
  }
  for (const entry of receipt.files) {
    const file = resolve(root, entry.path);
    if (entry.existed) fs.copyFileSync(resolve(receipt.backupRoot, entry.path), file);
    else fs.unlinkSync(file);
  }
}
const proof = { at: new Date().toISOString(), passed: true, files: receipt.files.length, sourceRestored: apply, productionChanged: false, privateRehearsal: rehearsal };
fs.writeFileSync(path.join(__dirname, '../evidence/source-rollback.json'), JSON.stringify(proof, null, 2) + '\n');
console.log(JSON.stringify(proof));
