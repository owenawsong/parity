"""Inspect a local iPad Bluetooth trace or a sysdiagnose archive.

    python tools/inspect-seal-trace.py --features both recording.tar.gz

Only selected diagnostic fields are printed. Feature IDs are documented in
LibrePods' AAP Definitions and R00tKited's AAP notes. The meaning of acoustic
seal response bytes is unknown. Compare captures with known Apple screen results.
"""

import argparse
import sys
import tarfile
from pathlib import Path

from airpods_trace import ClassicChannels, SEND_ACL, iter_traces, l2cap_packets


SEAL_PREFIX = bytes.fromhex("04 00 04 00 21 00")


def seal_line(direction, elapsed, cid, payload):
    if cid < 0x40 or not payload.startswith(SEAL_PREFIX):
        return None
    data = payload[len(SEAL_PREFIX):]
    route = "host -> buds" if direction == SEND_ACL else "buds -> host"
    description = "raw"
    if direction == SEND_ACL and len(data) >= 4 and int.from_bytes(data[2:4], "little") == len(data)-4:
        setup, operation = data[:2]
        description = f"setup=0x{setup:02x} op=0x{operation:02x} data"
        data = data[4:]
    # Keep unexpected large payloads local; only short diagnostic fields are useful here.
    value = data.hex(" ") if len(data) <= 128 else f"[{len(data)} bytes omitted]"
    return f"{elapsed:.3f}s  {route}  {description}={value}"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("recording", type=Path, help="a .pklg or Apple sysdiagnose .tar.gz")
    parser.add_argument("--features", choices=("seal", "transparency", "both"), default="seal")
    args = parser.parse_args()
    rows = seal_count = transparency_count = 0

    def emit(trace_number, line):
        nonlocal rows
        rows += 1
        if rows > 25000:
            raise ValueError("Output limit reached; use a shorter Bluetooth recording")
        print(f"trace {trace_number}  {line}")

    try:
        for number, stream in iter_traces(args.recording):
            transparency = None
            channels = ClassicChannels()
            if args.features in ("transparency", "both"):
                from transparency_trace import TransparencyTrace
                transparency = TransparencyTrace()
            for direction, at, connection, cid, payload in l2cap_packets(stream):
                if args.features in ("seal", "both"):
                    bearer = channels.port(direction, at, connection, cid, payload)
                    line = seal_line(direction, at, cid, payload) if bearer and bearer[0] == 0x1001 else None
                    if line is not None:
                        emit(number, line)
                        seal_count += 1
                if transparency is not None:
                    for line in transparency.observe(direction, at, connection, cid, payload):
                        emit(number, line)
                        transparency_count += 1
            if transparency is not None:
                for line in transparency.finish():
                    emit(number, line)
                    transparency_count += 1
    except (OSError, EOFError, tarfile.TarError, ValueError) as error:
        parser.exit(2, f"{error}\n")
    if args.features in ("seal", "both"):
        print(f"{seal_count} candidate acoustic seal frame(s).", file=sys.stderr)
        if not seal_count:
            print("No test exchange was identified on a confirmed AirPods channel. The trace must include the AirPods connection setup.", file=sys.stderr)
    if args.features in ("transparency", "both"):
        print(f"{transparency_count} Custom Transparency diagnostic line(s).", file=sys.stderr)


if __name__ == "__main__":
    main()
