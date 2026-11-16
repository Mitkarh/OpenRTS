#!/usr/bin/env python3
"""Run two real Godot clients through host/join, TLS, local menus and production."""
import argparse
from pathlib import Path
import socket
import subprocess
import tempfile
import time

parser = argparse.ArgumentParser()
parser.add_argument('godot')
parser.add_argument('--visible', action='store_true')
parser.add_argument('--output', type=Path)
args = parser.parse_args()
root = Path(__file__).resolve().parent.parent
output = (args.output or Path(tempfile.mkdtemp(prefix='openrts-network-'))).resolve()
output.mkdir(parents=True, exist_ok=True)
pin = output / 'host.pin'
pin.unlink(missing_ok=True)
with socket.socket() as probe:
    probe.bind(('127.0.0.1', 0))
    port = probe.getsockname()[1]
processes = []
logs = []
try:
    for role in ('host', 'join'):
        if role == 'join':
            deadline = time.monotonic() + 20
            while (not pin.exists() or len(pin.read_text().strip()) != 64) and time.monotonic() < deadline:
                if processes[0].poll() is not None:
                    raise RuntimeError('Host exited before publishing fingerprint')
                time.sleep(0.1)
            if not pin.exists() or len(pin.read_text().strip()) != 64:
                raise TimeoutError('Host did not publish fingerprint')
        command = [args.godot]
        if args.visible:
            command += ['--position', '20,20' if role == 'host' else '700,20', '--resolution', '900x700']
        else:
            command += ['--headless']
        command += ['--path', str(root / 'sandbox'), '--', '--verify-network=' + role,
                    '--network-port=' + str(port), '--network-pin-file=' + str(pin)]
        if args.visible:
            command += ['--capture=' + str(output / (role + '.png'))]
        log = (output / (role + '.log')).open('w')
        logs.append(log)
        processes.append(subprocess.Popen(command, stdout=log, stderr=subprocess.STDOUT))
    for process in processes:
        process.wait(timeout=55)
    hashes = []
    for role, process in zip(('host', 'join'), processes):
        text = (output / (role + '.log')).read_text()
        if process.returncode or 'OPENRTS_NETWORK_INPUT:' not in text or 'OPENRTS_NETWORK_MENU:' not in text:
            raise RuntimeError(role + ' failed:\n' + text)
        results = [line.split(':', 1)[1] for line in text.splitlines() if line.startswith('OPENRTS_NETWORK_PASS:')]
        if len(results) != 1:
            raise RuntimeError(role + ' missing final result:\n' + text)
        hashes.extend(results)
    if hashes[0] != hashes[1]:
        raise RuntimeError('Final hashes differ: ' + str(hashes))
    print('PASS Godot network: two TLS peers, local ownership, production, menu while ticks advance, graceful leave, offline replay')
    print('State hash:', hashes[0])
    print('Artifacts:', output)
finally:
    for process in processes:
        if process.poll() is None:
            process.kill()
            process.wait()
    for log in logs:
        log.close()
