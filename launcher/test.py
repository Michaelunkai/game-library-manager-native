"""Exercise the real Windows forwarding process, including CRT argument quoting."""
import base64
import json
import os
from pathlib import Path
import subprocess
import uuid

repo = Path(__file__).resolve().parent.parent
fixture = repo / 'evidence' / ('launcher-proof-' + uuid.uuid4().hex)
target = fixture / 'native/dist/GameLibrary.exe'
target.parent.mkdir(parents=True)
launcher = fixture / 'dist/GameLibrary.exe'
report = fixture / 'received arguments.txt'
source = fixture / 'Receiver.cs'
source.write_text('''using System; using System.IO; using System.Linq; using System.Text;
class Receiver { static int Main(string[] args) {
File.WriteAllLines(Environment.GetEnvironmentVariable("GLM_ARGUMENT_REPORT"),
args.Select(s => Convert.ToBase64String(Encoding.UTF8.GetBytes(s))).ToArray()); return 23; } }
''')
subprocess.run([r'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe',
    '-NoProfile', '-File', str(repo / 'launcher/build.ps1'), '-OutputPath', str(launcher)], check=True)
compiler = Path(os.environ['WINDIR']) / 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
subprocess.run([str(compiler), '/nologo', '/target:winexe', '/out:' + str(target), str(source)], check=True)
arguments = ['', '--data-dir', 'F:\\profile with spaces\\', 'embedded"quote', 'a\\"b', '日本語', 'trailing\\', 'plain']
environment = dict(os.environ, GLM_ARGUMENT_REPORT=str(report))
result = subprocess.run([str(launcher), *arguments], env=environment, timeout=20)
received = [base64.b64decode(line).decode('utf-8') for line in report.read_text(encoding='utf-8-sig').splitlines()]
checks = {'argumentsPreserved': received == arguments, 'childExitCodeReturned': result.returncode == 23,
          'noProfileCreated': not (fixture / 'data').exists()}
# Replacing only the native target must be picked up by the unchanged legacy
# entrypoint on its next launch; otherwise future releases could diverge again.
source.write_text(source.read_text().replace('return 23;', 'return 24;'))
subprocess.run([str(compiler), '/nologo', '/target:winexe', '/out:' + str(target), str(source)], check=True)
updated = subprocess.run([str(launcher), *arguments], env=environment, timeout=20)
checks['nextNativeUpdateUsedWithoutReplacingLauncher'] = updated.returncode == 24
receipt = {'fixture': str(fixture), 'launcher': str(launcher), 'checks': checks, 'passed': all(checks.values())}
(repo / 'evidence/criteria-legacy-launcher-tests.json').write_text(json.dumps(receipt, indent=2))
print(json.dumps(receipt))
assert receipt['passed'], receipt
