"""One isolated TCP relay: netem applies only to this container's eth0."""
import argparse
import json
import socket
import subprocess
import threading
import time

p = argparse.ArgumentParser()
p.add_argument('--upstream-port', type=int, required=True)
p.add_argument('--rtt', type=int, default=0)
p.add_argument('--jitter', type=int, default=0)
p.add_argument('--loss', type=float, default=0)
a = p.parse_args()
if not (1 <= a.upstream_port <= 65535 and 0 <= a.rtt <= 2000 and 0 <= a.jitter <= 500 and 0 <= a.loss <= 20):
    raise ValueError('Invalid impairment bounds')
upstream = socket.gethostbyname('host.docker.internal')
subprocess.run(['tc', 'qdisc', 'replace', 'dev', 'eth0', 'root', 'netem', 'limit', '256',
                'delay', f'{a.rtt / 2}ms', f'{a.jitter}ms', 'loss', f'{a.loss}%'], check=True)
listener = socket.socket()
listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
listener.bind(('0.0.0.0', 7000))
listener.listen(1)
print('READY', flush=True)
client, _ = listener.accept()
server = socket.create_connection((upstream, a.upstream_port), timeout=30)
server.settimeout(None)
for sock in (client, server):
    sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)

def forward(source, target):
    try:
        while True:
            data = source.recv(65536)
            if not data:
                break
            target.sendall(data)
    except OSError:
        pass
    finally:
        try:
            target.shutdown(socket.SHUT_WR)
        except OSError:
            pass

threads = [threading.Thread(target=forward, args=(client, server)),
           threading.Thread(target=forward, args=(server, client))]
for thread in threads:
    thread.start()
for thread in threads:
    thread.join()
print('NETEM_STATS:' + subprocess.check_output(['tc', '-s', '-j', 'qdisc', 'show', 'dev', 'eth0'], text=True), flush=True)
client.close()
server.close()
listener.close()
