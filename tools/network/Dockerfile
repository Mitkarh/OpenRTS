# Test-only tools, isolated Linux qdisc; no production dependency.
FROM golang:1.27.3-bookworm
RUN apt-get update && apt-get install -y --no-install-recommends iproute2 && rm -rf /var/lib/apt/lists/*
COPY netem_relay.py /netem_relay.py
ENTRYPOINT ["python3", "-u", "/netem_relay.py"]
CMD []
