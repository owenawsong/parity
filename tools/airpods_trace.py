"""Read Apple PacketLogger data locally. No files are extracted from an archive."""

import gzip
import struct
import tarfile
from pathlib import PurePosixPath


SEND_ACL, RECEIVE_ACL = 2, 3
MIB = 1 << 20
MAX_RECORD = 262144
MAX_TRACE = 512 * MIB
MAX_ARCHIVE = 2 * 1024 * MIB
MAX_EXPANDED = 8 * 1024 * MIB


class _LimitedReader:
    def __init__(self, stream, remaining):
        self.stream = stream
        self.remaining = remaining

    def read(self, size):
        data = self.stream.read(min(size, self.remaining + 1))
        self.remaining -= len(data)
        if self.remaining < 0:
            raise ValueError("The expanded archive exceeds the analysis limit")
        return data


class _BoundedTarInfo(tarfile.TarInfo):
    @classmethod
    def frombuf(cls, buf, encoding, errors):
        item = super().frombuf(buf, encoding, errors)
        if item.type in (tarfile.XHDTYPE, tarfile.XGLTYPE, tarfile.GNUTYPE_LONGNAME,
                         tarfile.GNUTYPE_LONGLINK, tarfile.SOLARIS_XHDTYPE) and item.size > MIB:
            raise ValueError("Oversized tar metadata")
        return item


def iter_traces(path):
    """Yield (trace number, open input). Consume each stream before advancing."""
    size = path.stat().st_size
    if size > MAX_ARCHIVE:
        raise ValueError("The input exceeds the 2 GiB archive limit")
    with path.open("rb") as source:
        signature = source.read(2)
        source.seek(0)
        compressed = signature == b"\x1f\x8b"
        if not compressed and path.suffix.lower() != ".tar":
            if size > MAX_TRACE:
                raise ValueError("PacketLogger file exceeds 512 MiB")
            yield 1, source
            return
        reader = gzip.GzipFile(fileobj=source) if compressed else source
        try:
            with tarfile.open(fileobj=_LimitedReader(reader, MAX_EXPANDED), mode="r|",
                              tarinfo=_BoundedTarInfo) as archive:
                traces = total = 0
                for number, item in enumerate(archive, 1):
                    if number > 100000:
                        raise ValueError("The archive contains too many entries")
                    name = PurePosixPath(item.name)
                    if (item.type not in (tarfile.REGTYPE, tarfile.AREGTYPE) or "\\" in item.name or
                            name.is_absolute() or ".." in name.parts or name.suffix.lower() != ".pklg"):
                        continue
                    traces += 1
                    total += item.size
                    if traces > 64 or item.size > MAX_TRACE or total > 2 * MAX_TRACE:
                        raise ValueError("Bluetooth traces exceed the archive analysis limit")
                    with archive.extractfile(item) as stream:
                        yield traces, stream
                if not traces:
                    raise ValueError("No regular .pklg Bluetooth trace found in this archive")
        finally:
            if compressed:
                reader.close()


def _records(stream):
    endian = None
    while header := stream.read(12):
        if len(header) != 12:
            raise ValueError("Truncated PacketLogger record header")
        if endian is None:
            candidates = [
                order for order in (">", "<")
                if 9 <= struct.unpack_from(order + "I", header)[0] <= MAX_RECORD
                and struct.unpack_from(order + "I", header, 8)[0] < 1000000
            ]
            if len(candidates) != 1:
                raise ValueError("Cannot identify the PacketLogger record byte order")
            endian = candidates[0]
        length, seconds, micros = struct.unpack(endian + "III", header)
        if not 9 <= length <= MAX_RECORD or micros >= 1000000:
            raise ValueError("Invalid PacketLogger length or timestamp")
        packet = stream.read(length - 8)
        if len(packet) != length - 8:
            raise ValueError("Truncated PacketLogger record")
        yield seconds * 1000000 + micros, packet[0], packet[1:]


def l2cap_packets(stream):
    """Yield completed L2CAP frames in capture order, timestamped at completion."""
    fragments = {}
    generations = {}
    start = None
    for timestamp, direction, acl in _records(stream):
        if start is None:
            start = timestamp
        if direction == 1 and len(acl) >= 6 and acl[:3] == b"\x05\x04\x00":
            # Disconnection Complete. The controller can reuse this connection handle.
            handle = struct.unpack_from("<H", acl, 3)[0] & 0xfff
            generations[handle] = generations.get(handle, 0) + 1
            for side in (SEND_ACL, RECEIVE_ACL):
                fragments.pop((side, handle), None)
        if direction not in (SEND_ACL, RECEIVE_ACL) or len(acl) < 4:
            continue
        flags, length = struct.unpack_from("<HH", acl)
        handle = flags & 0xfff
        key = direction, handle
        if length != len(acl) - 4:
            fragments.pop(key, None)
            continue
        boundary = (flags >> 12) & 3
        if boundary == 1:
            entry = fragments.pop(key, None)
            if entry is None:
                continue
            cid, total, buf = entry
            buf.extend(acl[4:])
        else:
            fragments.pop(key, None)
            if length < 4:
                continue
            total, cid = struct.unpack_from("<HH", acl, 4)
            buf = bytearray(acl[8:])
        if len(buf) == total:
            token = handle | (generations.get(handle, 0) << 12)
            yield direction, (timestamp - start) / 1000000, token, cid, bytes(buf)
        elif len(buf) < total:
            if len(fragments) >= 256:
                raise ValueError("Too many unfinished Bluetooth packets")
            fragments[key] = cid, total, buf


class ClassicChannels:
    """Track classic L2CAP channel requests and their successful responses."""

    def __init__(self):
        self.pending = {}
        self.endpoints = {}
        self.next_epoch = 0

    def _drop(self, key):
        previous = self.endpoints.pop(key, None)
        if previous:
            self.endpoints.pop(previous[2], None)

    def port(self, direction, elapsed, connection, cid, payload):
        """Return (PSM, stable bearer token) for a negotiated dynamic channel."""
        if cid != 1:
            known = self.endpoints.get((connection, direction, cid))
            return known[:2] if known else None
        reverse = RECEIVE_ACL if direction == SEND_ACL else SEND_ACL
        index = 0
        while index + 4 <= len(payload):
            code, ident, size = struct.unpack_from("<BBH", payload, index)
            index += 4
            body = payload[index:index + size]
            if len(body) != size:
                break
            index += size
            if code == 2 and size == 4:
                psm, local = struct.unpack("<HH", body)
                if local >= 0x40:
                    self._drop((connection, reverse, local))
                    self.pending[connection, direction, ident] = (psm, local)
            elif code == 3 and size == 8:
                remote, local, result, _ = struct.unpack("<HHHH", body)
                request = connection, reverse, ident
                if result == 1:  # Pending; wait for the terminal response.
                    continue
                pair = self.pending.pop(request, None)
                if result != 0 or not pair or pair[1] != local or remote < 0x40:
                    continue
                outbound, inbound = (connection, reverse, remote), (connection, direction, local)
                self._drop(outbound)
                self._drop(inbound)
                self.next_epoch += 1
                self.endpoints[outbound] = pair[0], self.next_epoch, inbound
                self.endpoints[inbound] = pair[0], self.next_epoch, outbound
            elif code in (6, 7) and size == 4:
                dest, source = struct.unpack("<HH", body)
                side = direction if code == 6 else reverse
                key = connection, side, dest
                current = self.endpoints.get(key)
                if current and current[2][2] == source:
                    self._drop(key)
            if len(self.pending) + len(self.endpoints) > 65536:
                raise ValueError("Too many Bluetooth channels in one trace")
        return None
