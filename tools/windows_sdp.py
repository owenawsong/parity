"""Read Classic SDP service records on one connected, paired Windows Bluetooth device."""
import ctypes as c
from ctypes import wintypes as w
import socket  # Initialize Winsock before calling the lookup functions.
import struct
import uuid


class DeviceInfo(c.Structure):
    _fields_ = [("size", w.DWORD), ("address", c.c_ulonglong), ("device_class", w.DWORD),
                ("connected", w.BOOL), ("remembered", w.BOOL), ("authenticated", w.BOOL),
                ("last_seen", w.WORD * 8), ("last_used", w.WORD * 8), ("name", w.WCHAR * 248)]


class Blob(c.Structure):
    _fields_ = [("length", w.DWORD), ("data", c.c_void_p)]


class Query(c.Structure):
    _fields_ = [("size", w.DWORD), ("name", c.c_void_p), ("service", c.c_void_p),
                ("version", c.c_void_p), ("comment", c.c_void_p), ("namespace", w.DWORD),
                ("provider", c.c_void_p), ("context", w.LPWSTR), ("protocol_count", w.DWORD),
                ("protocols", c.c_void_p), ("query", c.c_void_p), ("address_count", w.DWORD),
                ("addresses", c.c_void_p), ("flags", w.DWORD), ("blob", c.POINTER(Blob))]


def connected_device(address):
    get_info = c.WinDLL("bthprops.cpl").BluetoothGetDeviceInfo
    get_info.argtypes = [w.HANDLE, c.POINTER(DeviceInfo)]
    get_info.restype = w.DWORD
    info = DeviceInfo(size=c.sizeof(DeviceInfo), address=address)
    result = get_info(None, c.byref(info))
    if result:
        raise c.WinError(result)
    if not (info.authenticated and info.connected):
        raise ValueError(f"{info.name}: paired={bool(info.authenticated)}, connected={bool(info.connected)}")
    return info.name


def records(address):
    ws = c.WinDLL("Ws2_32")
    ws.WSALookupServiceBeginW.argtypes = [c.POINTER(Query), w.DWORD, c.POINTER(w.HANDLE)]
    ws.WSALookupServiceNextW.argtypes = [w.HANDLE, w.DWORD, c.POINTER(w.DWORD), c.c_void_p]
    ws.WSALookupServiceEnd.argtypes = [w.HANDLE]
    ws.WSAAddressToStringW.argtypes = [c.c_void_p, w.DWORD, c.c_void_p, w.LPWSTR, c.POINTER(w.DWORD)]
    sockaddr = struct.pack("<HQ16sI", 32, address, bytes(16), 0)
    name = c.create_unicode_buffer(128)
    length = w.DWORD(len(name))
    if ws.WSAAddressToStringW(sockaddr, len(sockaddr), None, name, c.byref(length)):
        raise c.WinError(ws.WSAGetLastError())
    l2cap = c.create_string_buffer(uuid.UUID("00000100-0000-1000-8000-00805f9b34fb").bytes_le)
    query = Query(size=c.sizeof(Query), service=c.addressof(l2cap), namespace=16, context=name.value)
    handle = w.HANDLE()
    return_blob, flush_cache = 0x200, 0x1000
    if ws.WSALookupServiceBeginW(c.byref(query), flush_cache | return_blob, c.byref(handle)):
        raise c.WinError(ws.WSAGetLastError())
    try:
        capacity = 32768
        while True:
            buf = c.create_string_buffer(capacity)
            size = w.DWORD(capacity)
            if ws.WSALookupServiceNextW(handle, return_blob, c.byref(size), buf):
                error = ws.WSAGetLastError()
                if error in (10110, 10102):  # WSA_E_NO_MORE / WSAENOMORE
                    return
                if error == 10014 and capacity < size.value <= 1024 * 1024:
                    capacity = size.value
                    continue
                raise c.WinError(error)
            blob = Query.from_buffer(buf).blob
            if not blob or not blob.contents.data or not blob.contents.length:
                raise ValueError("Winsock returned an empty service record")
            yield c.string_at(blob.contents.data, blob.contents.length)
    finally:
        ws.WSALookupServiceEnd(handle)
