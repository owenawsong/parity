"""Decode Custom Transparency exchanges on the AirPods Classic ATT bearer.

The handle and float layout are documented by LibrePods in ATTManager.kt and
Transparency.kt. Require Classic ATT and AAP on the same connection before
reporting a profile. EQ and extension bytes stay intact during comparisons.
"""
import math
import struct
from uuid import UUID
from dataclasses import dataclass, field

from airpods_trace import ClassicChannels

PROFILE = 0x0018
MAX_VALUE = 512  # ATT attribute value size limit.
REQUESTS = {2, 4, 6, 8, 10, 12, 14, 16, 18, 22, 24, 32}


def u16(data, offset=0):
    return int.from_bytes(data[offset:offset + 2], "little")


def discovery_identity(when, request, bounds, response):
    # Read By Type (characteristic declarations) and Read By Group Type
    # (primary services) carry a fixed-width array of little-endian entries.
    widths = (7, 21) if request == 0x08 else (6, 20)
    if not response or response[0] not in widths or len(response) == 1:
        return []
    width = response[0]
    if (len(response) - 1) % width:
        return []
    start, end = u16(bounds), u16(bounds, 2)
    matches = []
    previous = 0
    for i in range(1, len(response), width):
        entry = response[i:i + width]
        handle = u16(entry)
        if not start <= handle <= end or handle <= previous:
            return []
        previous = handle
        if request == 0x08:
            if u16(entry, 3) <= handle:
                return []
            if u16(entry, 3) != PROFILE:
                continue
            label, value = "characteristic", entry[5:]
        else:
            last = u16(entry, 2)
            if last < handle:
                return []
            if not handle <= PROFILE <= last:
                continue
            label, value = "primary service", entry[4:]
            previous = last
        identifier = f"{u16(value):04x}" if len(value) == 2 else str(UUID(bytes=value[::-1]))
        matches.append(f"{when:.3f}s  transparency: discovered {label} UUID {identifier} covering handle 0x0018")
    return matches


def profile(raw):
    if len(raw) not in (100, 104):
        return None
    v = struct.unpack(f"<{len(raw) // 4}f", raw)
    if not all(map(math.isfinite, v)) or any(v[i] not in (0, 1) for i in (0, 11, 23)):
        return None
    values = {
        "enabled": bool(v[0]),
        "amplification L/R": (v[9], v[21]),
        "tone L/R": (v[10], v[22]),
        "conversation boost L/R": (bool(v[11]), bool(v[23])),
        "ambient reduction L/R": (v[12], v[24]),
    }
    if len(v) == 26:
        values["own voice"] = v[25]
    return values


def display(value):
    if isinstance(value, tuple):
        return "/".join(display(part) for part in value)
    if isinstance(value, bool):
        return "on" if value else "off"
    return format(value, ".5g")


@dataclass
class Bearer:
    request: tuple | None = None
    reading: bytearray | None = None
    read_time: float = 0
    prepared: bytearray = field(default_factory=bytearray)
    prepare_valid: bool = True
    last_device: bytes | None = None
    last_write: bytes | None = None

    def report(self, when, source, raw, from_device):
        raw = bytes(raw)
        values = profile(raw)
        if values is None:
            return [f"{when:.3f}s  transparency: {source}, unrecognized profile ({len(raw)} bytes)"]
        previous = profile(self.last_device) if self.last_device is not None else {}
        parts = []
        for name, value in values.items():
            if name not in previous:
                parts.append(f"{name}={display(value)}")
            elif previous[name] != value:
                parts.append(f"{name} {display(previous[name])} -> {display(value)}")
        if self.last_device is not None:
            old = self.last_device
            if old[4:36] != raw[4:36] or old[52:84] != raw[52:84]:
                parts.append("EQ data changed")
            if len(old) != len(raw):
                parts.append(f"profile size {len(old)} -> {len(raw)}")
        if from_device:
            self.last_device = raw
            if source == "device read" and self.last_write is not None:
                parts.append("matches preceding write" if self.last_write == raw else "differs from preceding write")
                self.last_write = None
        else:
            self.last_write = raw
        return [f"{when:.3f}s  transparency: {source}; " + (", ".join(parts) or "unchanged")]

    def finish_read(self):
        out = []
        if self.reading is not None and self.request is None:
            out = self.report(self.read_time, "device read", self.reading, True)
        self.reading = None
        return out

    def host(self, when, op, body):
        if op == 0x52:  # Write commands have no acknowledgement.
            if len(body) >= 2 and u16(body) == PROFILE:
                lines = self.finish_read() if self.request is None else []
                return lines + self.report(when, "host write command (unacknowledged)", body[2:], False)
            return []
        if op not in REQUESTS:
            return []
        blob = op == 0x0c and len(body) == 4 and u16(body) == PROFILE
        out = [] if blob else self.finish_read()
        self.request = None
        if op == 0x0a and len(body) == 2 and u16(body) == PROFILE:
            self.reading = bytearray()
            self.request = (op, b"")
        elif (len(body) == 6 and 0 < u16(body) <= u16(body, 2)
              and (op, u16(body, 4)) in ((0x08, 0x2803), (0x10, 0x2800))):
            self.request = (op, body)
        elif blob:
            if self.reading is not None and u16(body, 2) == len(self.reading):
                self.request = (op, b"")
            else:
                self.reading = None
        elif op in (0x12, 0x16) and len(body) >= (2 if op == 0x12 else 4) and u16(body) == PROFILE:
            if len(body) <= MAX_VALUE + 4:
                self.request = (op, body)
        elif op == 0x18 and body in (b"\x01", b"\x00"):
            if self.prepared or not self.prepare_valid:
                self.request = (op, body)
        return out

    def buds(self, when, op, body):
        if op in (0x1b, 0x1d):  # These PDUs may be interleaved with the reply to a request.
            if len(body) >= 2 and u16(body) == PROFILE:
                lines = self.finish_read() if self.request is None and self.reading is not None and profile(self.reading) else []
                kind = "device notification" if op == 0x1b else "device indication"
                return lines + self.report(when, kind, body[2:], True)
            return []
        if self.request is None:
            return []
        req, expected = self.request
        if op == 1 and len(body) == 4 and body[0] == req:
            self.request = None
            if req == 0x0c and u16(body, 1) == PROFILE and body[3] in (0x07, 0x0b):
                return self.finish_read()
            if req in (0x0a, 0x0c):
                self.reading = None
            if req == 0x16:
                self.prepare_valid = False
            if req == 0x18:
                self.prepared.clear()
                self.prepare_valid = True
            if req in (0x08, 0x10):
                return []
            return [f"{when:.3f}s  transparency: ATT request 0x{req:02x} rejected (0x{body[3]:02x})"]
        if op != req + 1:
            return []
        self.request = None
        if req in (0x0a, 0x0c) and self.reading is not None:
            self.read_time = when
            if len(self.reading) + len(body) <= MAX_VALUE:
                self.reading.extend(body)
            else:
                self.reading = None
        elif req in (0x08, 0x10):
            return discovery_identity(when, req, expected, body)
        elif req == 0x12 and not body:
            return self.report(when, "host write acknowledged", expected[2:], False)
        elif req == 0x16:
            if body == expected and u16(body, 2) == len(self.prepared) and len(self.prepared) + len(body) - 4 <= MAX_VALUE:
                self.prepared.extend(body[4:])
            else:
                self.prepare_valid = False
        elif req == 0x18:
            out = []
            if expected == b"\x01" and not body and self.prepare_valid and self.prepared:
                out = self.report(when, "host prepared write acknowledged", self.prepared, False)
            self.prepared.clear()
            self.prepare_valid = True
            return out
        return []


class TransparencyTrace:
    def __init__(self):
        self.channels = ClassicChannels()
        self.bearers = {}
        self.aap_connections = set()
        self.deferred = {}
        self.deferred_count = 0

    def observe(self, direction, elapsed, connection, cid, payload):
        route = self.channels.port(direction, elapsed, connection, cid, payload)
        if not route or not payload:
            return []
        if route[0] == 0x1001 and len(payload) >= 6 and payload.startswith(b"\x04\x00\x04\x00"):
            self.aap_connections.add(connection)
            out = self.deferred.pop(connection, [])
            self.deferred_count -= len(out)
            return out
        if route[0] != 0x1f:
            return []
        key = (connection, route[1])
        if key not in self.bearers:
            self.bearers[key] = Bearer()
        bearer = self.bearers[key]
        op, body = payload[0], payload[1:]
        lines = bearer.host(elapsed, op, body) if direction == 2 else bearer.buds(elapsed, op, body)
        if connection in self.aap_connections:
            return lines
        if lines:
            # Initial profile reads can precede AAP setup. Another GATT device
            # can use handle 0x18 for an entirely unrelated characteristic.
            self.deferred_count += len(lines)
            if self.deferred_count > 25000:
                raise ValueError("Too many unverified ATT events in one trace")
            self.deferred.setdefault(connection, []).extend(lines)
        return []

    def finish(self):
        return [line for (connection, _), bearer in self.bearers.items()
                if connection in self.aap_connections for line in bearer.finish_read()]
