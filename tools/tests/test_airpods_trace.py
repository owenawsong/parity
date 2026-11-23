"""Regression coverage for local iPad archive filtering and L2CAP channel identity."""

import gzip
import io
import struct
import subprocess
import sys
import tarfile
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

TOOLS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(TOOLS))
from airpods_trace import ClassicChannels, iter_traces, l2cap_packets


def record(direction, packet, order=">", millis=0):
    return struct.pack(order + "III", 9 + len(packet), 1234, 1000 * millis) + bytes([direction]) + packet


def acl(body, boundary=2):
    return struct.pack("<HH", (boundary << 12) | 12, len(body)) + body


def l2(cid, body):
    return struct.pack("<HH", len(body), cid) + body


def archive_bytes(members):
    buf = io.BytesIO()
    with tarfile.open(fileobj=buf, mode="w") as target:
        for name, data in members:
            item = tarfile.TarInfo(name)
            if isinstance(data, str):
                item.type = tarfile.SYMTYPE
                item.linkname = data
                target.addfile(item)
            else:
                item.size = len(data)
                target.addfile(item, io.BytesIO(data))
    return gzip.compress(buf.getvalue())


REQUEST = bytes.fromhex("02 01 04 00 01 10 41 00")
ACCEPT = bytes.fromhex("03 01 08 00 50 00 41 00 00 00 00 00")
PREFIX = bytes.fromhex("04 00 04 00 21 00")


def fixture(order=">"):
    packet = l2(0x50, PREFIX + bytes.fromhex("01 02 01 00 ff"))
    private = PREFIX + b"private"
    return b"".join([
        record(2, acl(l2(1, REQUEST)), order),
        record(3, acl(l2(1, ACCEPT)), order, 1),
        record(2, acl(packet[:10]), order, 2),
        record(3, acl(l2(0x41, PREFIX + b"\x03")), order, 3),
        record(2, acl(packet[10:], 1), order, 4),
        record(2, acl(l2(0x70, private)), order, 5),
        record(1, bytes.fromhex("05 04 00 0c 00 13"), order, 6),
        record(2, acl(l2(0x50, private)), order, 7),
    ])


class TraceTests(unittest.TestCase):
    def test_channel_identity_and_handle_reuse(self):
        for order in ("<", ">"):
            channels = ClassicChannels()
            results = [(channels.port(*p), p[1]) for p in l2cap_packets(io.BytesIO(fixture(order)))]
            self.assertEqual(results, [(None, 0), (None, .001), ((0x1001, 1), .003),
                                       ((0x1001, 1), .004), (None, .005), (None, .007)])

    def test_bearer_requires_matching_success_and_direction(self):
        channels = ClassicChannels()
        def port(direction, cid, data=b""):
            return channels.port(direction, 0, 12, cid, data)
        port(3, 1, REQUEST)
        port(3, 1, ACCEPT)
        self.assertIsNone(port(3, 0x50))
        port(2, 1, ACCEPT[:8] + bytes.fromhex("01 00 00 00"))
        self.assertIsNone(port(3, 0x50))
        port(2, 1, ACCEPT)
        self.assertEqual(port(3, 0x50), (0x1001, 1))
        self.assertEqual(port(2, 0x41), (0x1001, 1))
        port(3, 1, bytes.fromhex("06 02 04 00 50 00 41 00"))
        self.assertIsNone(port(2, 0x41))
        self.assertIsNone(port(3, 0x50))
        port(3, 1, REQUEST)
        port(2, 1, ACCEPT)
        self.assertEqual(port(3, 0x50), (0x1001, 2))

    def test_archive_cli_skips_unrelated_and_unsafe_members(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "sysdiagnose_example.tar.gz"
            path.write_bytes(archive_bytes([
                ("../../unrelated.pklg", b"not a trace"),
                ("escape.pklg", "/tmp/link"),
                ("logs/private.txt", b"unrelated"),
                ("sysdiagnose/logs/Bluetooth/bluetoothd-hci-latest.pklg", fixture()),
            ]))
            command = [sys.executable, str(TOOLS / "inspect-seal-trace.py"), "--features", "both", str(path)]
            result = subprocess.run(command, text=True, capture_output=True, check=True)
            self.assertEqual(result.stdout.splitlines(),
                             ["trace 1  0.003s  buds -> host  raw=03",
                              "trace 1  0.004s  host -> buds  setup=0x01 op=0x02 data=ff"])
            self.assertIn("2 candidate acoustic seal frame(s)", result.stderr)
            self.assertIn("0 Custom Transparency diagnostic line(s)", result.stderr)
            self.assertEqual(list(Path(folder).iterdir()), [path])
            with patch("airpods_trace.MAX_EXPANDED", 32), self.assertRaisesRegex(ValueError, "limit"):
                list(iter_traces(path))

    def test_truncated_file(self):
        with self.assertRaisesRegex(ValueError, "Truncated"):
            list(l2cap_packets(io.BytesIO(fixture()[:-2])))


if __name__ == "__main__":
    unittest.main()
