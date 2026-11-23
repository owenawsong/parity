import struct
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from transparency_trace import TransparencyTrace


TARGET = b"\x18\x00"


def settings(size=104, amp=0, tone=0, eq=0):
    fields = [0.0] * (size // 4)
    fields[0] = 1.0
    fields[9] = fields[21] = amp
    fields[10] = fields[22] = tone
    fields[14] = eq
    return struct.pack(f"<{len(fields)}f", *fields)


class TransparencyTests(unittest.TestCase):
    def setUp(self):
        self.trace = TransparencyTrace()
        self.lines = []
        self.t = 0
        self.identify()
        self.connect(31)

    def packet(self, direction, cid, data, connection=1):
        self.t += 1
        self.lines.extend(self.trace.observe(direction, self.t, connection, cid, data))

    def connect(self, psm):
        self.packet(2, 1, struct.pack("<BBHHH", 2, 9, 4, psm, 0x40))
        self.packet(3, 1, struct.pack("<BBHHHHH", 3, 9, 8, 0x41, 0x40, 0, 0))

    def identify(self, connection=1):
        self.packet(2, 1, struct.pack("<BBHHH", 2, 3, 4, 0x1001, 0x50), connection)
        self.packet(3, 1, struct.pack("<BBHHHHH", 3, 3, 8, 0x51, 0x50, 0, 0), connection)
        self.packet(3, 0x50, b"\x04\x00\x04\x00\x06\x00", connection)

    def host(self, data):
        self.packet(2, 0x41, data)

    def buds(self, data):
        self.packet(3, 0x40, data)

    def read(self, raw):
        self.host(b"\x0a" + TARGET)
        self.buds(b"\x0b" + raw[:56])
        self.host(b"\x0c" + TARGET + struct.pack("<H", 56))
        self.buds(b"\x0d" + raw[56:])

    def prepare(self, raw, echo=True):
        for offset in range(0, len(raw), 44):
            fragment = TARGET + struct.pack("<H", offset) + raw[offset:offset + 44]
            self.host(b"\x16" + fragment)
            self.buds(b"\x17" + (fragment if echo else fragment[:-1]))

    def test_acknowledged_write_then_matching_fragmented_readback(self):
        self.read(settings())
        changed = settings(amp=.25, tone=-.125)
        self.host(b"\x12" + TARGET + changed)
        self.buds(b"\x13")
        self.read(changed)
        self.lines.extend(self.trace.finish())
        self.assertEqual(3, len(self.lines))
        self.assertIn("host write acknowledged", self.lines[1])
        self.assertIn("amplification L/R 0/0 -> 0.25/0.25", self.lines[1])
        self.assertIn("tone L/R 0/0 -> -0.125/-0.125", self.lines[1])
        self.assertIn("matches preceding write", self.lines[2])

    def test_unacknowledged_command_differs_from_subsequent_read(self):
        self.read(settings(100))
        self.host(b"\x52" + TARGET + settings(100, eq=99.125))
        self.read(settings(100))
        self.lines.extend(self.trace.finish())
        self.assertEqual(3, len(self.lines))
        self.assertIn("unacknowledged", self.lines[1])
        self.assertIn("EQ data changed", self.lines[1])
        self.assertNotIn("99.125", " ".join(self.lines))
        self.assertIn("differs from preceding write", self.lines[2])

    def test_echoed_long_write_requires_successful_execution(self):
        self.prepare(settings())
        self.host(b"\x18\x00")  # Cancel.
        self.buds(b"\x19")
        self.prepare(settings(), echo=False)
        self.host(b"\x18\x01")
        self.buds(b"\x19")
        self.assertFalse(self.lines)
        self.prepare(settings(100))
        self.host(b"\x18\x01")
        self.assertFalse(self.lines)
        self.buds(b"\x19")
        self.assertIn("prepared write acknowledged", self.lines[0])

    def test_unrelated_channels_handles_and_unfinished_reads(self):
        self.host(b"\x0a\x42\x00")
        self.buds(b"\x0b" + settings())
        self.packet(2, 0x41, b"\x0a" + TARGET, connection=2)
        self.packet(3, 0x40, b"\x0b" + settings(), connection=2)
        self.host(b"\x0a" + TARGET)
        self.buds(b"\x0b" + settings()[:42])
        self.host(b"\x0c" + TARGET + b"\x2b\x00")  # Gap.
        self.buds(b"\x0d" + settings()[43:])
        self.connect(0x27)  # Reuse the same CID for another service.
        self.read(settings())
        self.assertFalse(self.lines + self.trace.finish())

    def test_rejected_write_never_marked_as_readback(self):
        self.host(b"\x12" + TARGET + settings())
        self.buds(b"\x01\x12" + TARGET + b"\x03")
        self.read(settings())
        self.lines.extend(self.trace.finish())
        self.assertEqual(2, len(self.lines))
        self.assertIn("rejected", self.lines[0])
        self.assertNotIn("preceding write", self.lines[1])

    def test_unsupported_size_and_nonfinite_values_are_sanitized(self):
        self.buds(b"\x1b" + TARGET + b"private")
        self.buds(b"\x1d" + TARGET + settings(amp=float("nan")))
        self.assertEqual(2, len(self.lines))
        self.assertTrue(all("unrecognized profile" in line for line in self.lines))
        self.assertNotIn("private", " ".join(self.lines))

    def test_discovery_reports_characteristic_and_enclosing_service(self):
        self.host(bytes.fromhex("10 0100 ffff 0028"))
        self.buds(bytes.fromhex("11 06 0100 1000 0118 1100 2200 f0ff"))
        self.host(bytes.fromhex("08 1100 2200 0328"))
        self.buds(bytes.fromhex("09 15 1700 12 1800 ffeeddccbbaa99887766554433221100"))
        self.assertIn("primary service UUID fff0", self.lines[0])
        self.assertIn("characteristic UUID 00112233-4455-6677-8899-aabbccddeeff", self.lines[1])

    def test_unrelated_or_malformed_discovery_does_not_identify_profile(self):
        self.host(bytes.fromhex("08 2100 2300 0328"))
        self.buds(bytes.fromhex("09 07 1700 12 1800 1234"))
        self.host(bytes.fromhex("10 0100 ffff 0028"))
        self.buds(bytes.fromhex("11 06 1100 2200 f0ff 00"))
        self.assertFalse(self.lines)

    def test_generic_att_device_requires_aap_on_same_connection(self):
        self.trace = TransparencyTrace()
        self.connect(31)
        self.buds(b"\x1b" + TARGET + settings())
        self.identify(connection=7)
        self.assertFalse(self.lines + self.trace.finish())
        self.identify(connection=1)
        self.assertIn("device notification", self.lines[0])


if __name__ == "__main__":
    unittest.main()
