"""Optional native 3DES-CBC via Windows CNG (bcrypt.dll), reached through stdlib ctypes.

Pure-Python DES costs ~4 s on a 275 KB story save, which is enough to feel slow in an
interactive editor. Windows already ships a 3DES implementation, and ctypes is stdlib, so
using it keeps the "no third-party packages" property while running ~100x faster.

`available()` reports whether this backend can be used; pydes falls back to its own
implementation everywhere else (and the two are cross-checked in pydes' self-tests).
"""
import ctypes
from ctypes import wintypes

try:
    _bcrypt = ctypes.WinDLL("bcrypt")
except (OSError, AttributeError):      # not Windows
    _bcrypt = None

BCRYPT_BLOCK_PADDING = 0x00000001


def available():
    return _bcrypt is not None


if _bcrypt is not None:
    _bcrypt.BCryptOpenAlgorithmProvider.argtypes = [
        ctypes.POINTER(ctypes.c_void_p), wintypes.LPCWSTR, wintypes.LPCWSTR, wintypes.ULONG]
    _bcrypt.BCryptSetProperty.argtypes = [
        ctypes.c_void_p, wintypes.LPCWSTR, ctypes.c_char_p, wintypes.ULONG, wintypes.ULONG]
    _bcrypt.BCryptGenerateSymmetricKey.argtypes = [
        ctypes.c_void_p, ctypes.POINTER(ctypes.c_void_p), ctypes.c_char_p, wintypes.ULONG,
        ctypes.c_char_p, wintypes.ULONG, wintypes.ULONG]
    _bcrypt.BCryptEncrypt.argtypes = [
        ctypes.c_void_p, ctypes.c_char_p, wintypes.ULONG, ctypes.c_void_p,
        ctypes.c_char_p, wintypes.ULONG, ctypes.c_char_p, wintypes.ULONG,
        ctypes.POINTER(wintypes.ULONG), wintypes.ULONG]
    _bcrypt.BCryptDecrypt.argtypes = _bcrypt.BCryptEncrypt.argtypes
    _bcrypt.BCryptDestroyKey.argtypes = [ctypes.c_void_p]
    _bcrypt.BCryptCloseAlgorithmProvider.argtypes = [ctypes.c_void_p, wintypes.ULONG]


class _Alg:
    """Open a 3DES-CBC algorithm handle and a key, and clean both up."""

    def __init__(self, key):
        self.h_alg = ctypes.c_void_p()
        self.h_key = ctypes.c_void_p()
        st = _bcrypt.BCryptOpenAlgorithmProvider(
            ctypes.byref(self.h_alg), "3DES", None, 0)
        if st != 0:
            raise OSError(f"BCryptOpenAlgorithmProvider failed: 0x{st & 0xFFFFFFFF:08x}")
        mode = "ChainingModeCBC".encode("utf-16-le") + b"\x00\x00"
        st = _bcrypt.BCryptSetProperty(self.h_alg, "ChainingMode", mode, len(mode), 0)
        if st != 0:
            self.close()
            raise OSError(f"BCryptSetProperty failed: 0x{st & 0xFFFFFFFF:08x}")
        st = _bcrypt.BCryptGenerateSymmetricKey(
            self.h_alg, ctypes.byref(self.h_key), None, 0, key, len(key), 0)
        if st != 0:
            self.close()
            raise OSError(f"BCryptGenerateSymmetricKey failed: 0x{st & 0xFFFFFFFF:08x}")

    def close(self):
        if self.h_key:
            _bcrypt.BCryptDestroyKey(self.h_key)
            self.h_key = ctypes.c_void_p()
        if self.h_alg:
            _bcrypt.BCryptCloseAlgorithmProvider(self.h_alg, 0)
            self.h_alg = ctypes.c_void_p()

    def __enter__(self):
        return self

    def __exit__(self, *a):
        self.close()


def _run(fn, key, iv, data, flags):
    with _Alg(key) as a:
        iv_buf = ctypes.create_string_buffer(bytes(iv), len(iv))   # CNG mutates the IV
        out_len = wintypes.ULONG(0)
        st = fn(a.h_key, data, len(data), None, iv_buf, len(iv),
                None, 0, ctypes.byref(out_len), flags)
        if st != 0:
            raise OSError(f"size query failed: 0x{st & 0xFFFFFFFF:08x}")

        out = ctypes.create_string_buffer(out_len.value)
        iv_buf = ctypes.create_string_buffer(bytes(iv), len(iv))   # reset after the query
        written = wintypes.ULONG(0)
        st = fn(a.h_key, data, len(data), None, iv_buf, len(iv),
                out, out_len.value, ctypes.byref(written), flags)
        if st != 0:
            raise OSError(f"crypt failed: 0x{st & 0xFFFFFFFF:08x}")
        return out.raw[:written.value]


def cbc_encrypt(key, iv, data):
    """3DES-CBC encrypt with PKCS7 padding."""
    return _run(_bcrypt.BCryptEncrypt, key, iv, data, BCRYPT_BLOCK_PADDING)


def cbc_decrypt(key, iv, data):
    """3DES-CBC decrypt, PKCS7 padding stripped."""
    return _run(_bcrypt.BCryptDecrypt, key, iv, data, BCRYPT_BLOCK_PADDING)


if __name__ == "__main__":
    print("bcrypt available:", available())
    if available():
        key, iv = bytes(range(24)), bytes(range(8))
        msg = b"native backend round-trip" * 10
        assert cbc_decrypt(key, iv, cbc_encrypt(key, iv, msg)) == msg
        print("native round-trip OK")
