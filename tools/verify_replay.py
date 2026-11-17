#!/usr/bin/env python3
"""Record a real TLS match, then exercise its replay through native Godot controls."""
import argparse
from pathlib import Path
import subprocess
import tempfile

parser = argparse.ArgumentParser()
parser.add_argument('godot')
parser.add_argument('--visible', action='store_true')
parser.add_argument('--output', type=Path)
args = parser.parse_args()
root = Path(__file__).resolve().parent.parent
output = (args.output or Path(tempfile.mkdtemp(prefix='openrts-replay-'))).resolve()
output.mkdir(parents=True, exist_ok=True)
with (output / 'record.log').open('w') as log:
    subprocess.run(['python3', str(root / 'tools/verify_network.py'), args.godot, '--host-fog-hold=7', '--output=' + str(output / 'match')],
                   cwd=root, stdout=log, stderr=subprocess.STDOUT, check=True, timeout=90)
text = (output / 'match/host.log').read_text()
paths = [line.split(':', 1)[1] for line in text.splitlines() if line.startswith('OPENRTS_NETWORK_JOURNAL:')]
if len(paths) != 1:
    raise RuntimeError('Recorded match did not publish its journal path')
command = [args.godot]
if not args.visible:
    command += ['--headless']
else:
    command += ['--resolution', '1280x800']
command += ['--path', str(root / 'sandbox'), '--', '--verify-replay=' + paths[0]]
if args.visible:
    command += ['--capture=' + str(output / 'replay.png')]
with (output / 'replay.log').open('w') as log:
    result = subprocess.run(command, cwd=root, stdout=log, stderr=subprocess.STDOUT, timeout=45)
text = (output / 'replay.log').read_text()
if result.returncode or 'OPENRTS_REPLAY_PASS:' not in text:
    raise RuntimeError('Godot replay failed:\n' + text)
print('PASS Godot replay: embedded recipe, pause/step/speed, seek both directions, player POV, read-only commands, final hash')
print('Artifacts:', output)
