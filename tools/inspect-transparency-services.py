"""Inspect cached or live Windows Bluetooth SDP records for paired AirPods."""
import argparse
import re
import winreg

from collections import defaultdict
from uuid import UUID

DEVICES = r"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices"
TRANSPARENCY_HANDLE = 0x18  # LibrePods' known layout. An overlapping range alone is inconclusive.


def elements(data):
    offset = 0
    while offset < len(data):
        kind, size_index = divmod(data[offset], 8)
        offset += 1
        if size_index < 5:
            length = 0 if kind == 0 else 1 << size_index
        else:
            width = 1 << (size_index - 5)
            if offset + width > len(data):
                raise ValueError("Incomplete SDP length")
            length = int.from_bytes(data[offset:offset + width], "big")
            offset += width
        if offset + length > len(data):
            raise ValueError("Incomplete SDP element")
        yield kind, data[offset:offset + length]
        offset += length


def att_range(record):
    root = list(elements(record))
    if len(root) != 1 or root[0][0] != 6:
        return None
    attrs = iter(elements(root[0][1]))
    pairs = {}
    for kind, key in attrs:
        value = next(attrs, None)
        if kind != 1 or len(key) != 2 or value is None:
            raise ValueError("Malformed SDP attribute")
        pairs[int.from_bytes(key, "big")] = value
    if 4 not in pairs or pairs[4][0] != 6:
        return None
    layers = [list(elements(seq)) for kind, seq in elements(pairs[4][1]) if kind == 6]
    if [(3, bytes.fromhex("0100")), (1, bytes.fromhex("001f"))] not in layers:
        return None
    for layer in layers:
        if len(layer) == 3 and layer[0] == (3, bytes.fromhex("0007")):
            if not all(kind == 1 and len(value) == 2 for kind, value in layer[1:]):
                continue
            lo, hi = (int.from_bytes(value, "big") for _, value in layer[1:])
            if not 0 < lo <= hi:
                continue
            classes = []
            if 1 in pairs and pairs[1][0] == 6:
                for kind, value in elements(pairs[1][1]):
                    if kind == 3:
                        classes.append(str(UUID(bytes=value)) if len(value) == 16 else value.hex())
            return lo, hi, ", ".join(classes) or "unspecified"
    return None


def inspect(match):
    found = False
    with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, DEVICES) as devices:
        for n in range(winreg.QueryInfoKey(devices)[0]):
            with winreg.OpenKey(devices, winreg.EnumKey(devices, n)) as device:
                try:
                    name = winreg.QueryValueEx(device, "Name")[0]
                except FileNotFoundError:
                    continue
                if isinstance(name, bytes):
                    name = name.decode("utf-8", errors="replace").rstrip("\0")
                if not isinstance(name, str) or match.casefold() not in name.casefold():
                    continue
                found = True
                print(f"{name}  ({winreg.EnumKey(devices, n)})")
                ranges = defaultdict(list)
                for source in ("CachedServices", "DynamicCachedServices"):
                    try:
                        cache = winreg.OpenKey(device, source)
                    except FileNotFoundError:
                        continue
                    with cache:
                        for i in range(winreg.QueryInfoKey(cache)[1]):
                            _, value, value_type = winreg.EnumValue(cache, i)
                            if value_type != winreg.REG_BINARY:
                                continue
                            try:
                                item = att_range(value)
                            except ValueError:
                                print(f"  Skipped malformed record in {source}.")
                                continue
                            if item and source not in ranges[item]:
                                ranges[item].append(source)
                for (lo, hi, service), sources in sorted(ranges.items()):
                    print(f"  {lo:#06x}..{hi:#06x}  service {service}  ({', '.join(sources)})")
                matches = any(lo <= TRANSPARENCY_HANDLE <= hi for lo, hi, _ in ranges)
                print("  Known handle falls inside an advertised range; profile needs device verification."
                      if matches else "  Known Transparency handle is outside the available cached ranges.")
    if not found:
        print("No paired device matched that name.")
    return found


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--name", default="AirPod", help="Substring of the paired device's Windows name")
    parser.add_argument("--live", metavar="ADDRESS", help="Query fresh Classic SDP records on this connected paired address")
    args = parser.parse_args()
    if args.live is None:
        raise SystemExit(0 if inspect(args.name) else 1)

    from windows_sdp import connected_device, records

    if not re.fullmatch(r"[0-9a-fA-F]{12}|(?:[0-9a-fA-F]{2}:){5}[0-9a-fA-F]{2}", args.live):
        parser.error("--live expects a 12-digit Bluetooth address, optionally colon-separated")
    address = int(args.live.replace(":", ""), 16)
    try:
        print(f"{connected_device(address)}  (paired and connected)")
        count = 0
        for record in records(address):
            root = list(elements(record))
            if len(root) != 1 or root[0][0] != 6:
                raise ValueError("Unrecognized SDP response format")
            count += 1
            item = att_range(record)
            if item:
                lo, hi, service = item
                print(f"  {lo:#06x}..{hi:#06x}  service {service}")
        if count == 0:
            raise ValueError("No live SDP records returned")
        connected_device(address)
        print(f"{count} live records. Advertised ranges alone cannot establish a readable profile.")
    except (OSError, ValueError) as exc:
        parser.exit(1, f"Live inventory incomplete: {exc}\n")
