"""Pure-Python DES / TripleDES (EDE3) with CBC mode + PKCS7. Stdlib only.

Eiyuden Chronicle encrypts its saves with TripleDES-CBC/PKCS7 (24-byte key, 8-byte IV),
recovered from Rising.CryptoHelper at runtime. This module lets the editor read and write
those saves with no third-party dependencies.

The round function uses precomputed SP-boxes (S-box output already P-permuted) and
integer bit ops rather than per-bit lists, which is what makes a 275 KB story save
practical to load in an interactive tool.
"""

# --- DES tables -----------------------------------------------------------------
_IP = [58,50,42,34,26,18,10,2, 60,52,44,36,28,20,12,4,
       62,54,46,38,30,22,14,6, 64,56,48,40,32,24,16,8,
       57,49,41,33,25,17, 9,1, 59,51,43,35,27,19,11,3,
       61,53,45,37,29,21,13,5, 63,55,47,39,31,23,15,7]

_FP = [40,8,48,16,56,24,64,32, 39,7,47,15,55,23,63,31,
       38,6,46,14,54,22,62,30, 37,5,45,13,53,21,61,29,
       36,4,44,12,52,20,60,28, 35,3,43,11,51,19,59,27,
       34,2,42,10,50,18,58,26, 33,1,41,9,49,17,57,25]

_P = [16,7,20,21,29,12,28,17, 1,15,23,26,5,18,31,10,
      2,8,24,14,32,27,3,9, 19,13,30,6,22,11,4,25]

_PC1 = [57,49,41,33,25,17,9, 1,58,50,42,34,26,18,
        10,2,59,51,43,35,27, 19,11,3,60,52,44,36,
        63,55,47,39,31,23,15, 7,62,54,46,38,30,22,
        14,6,61,53,45,37,29, 21,13,5,28,20,12,4]

_PC2 = [14,17,11,24,1,5, 3,28,15,6,21,10, 23,19,12,4,26,8,
        16,7,27,20,13,2, 41,52,31,37,47,55, 30,40,51,45,33,48,
        44,49,39,56,34,53, 46,42,50,36,29,32]

_SHIFTS = [1,1,2,2,2,2,2,2,1,2,2,2,2,2,2,1]

_SBOXES = [
[[14,4,13,1,2,15,11,8,3,10,6,12,5,9,0,7],[0,15,7,4,14,2,13,1,10,6,12,11,9,5,3,8],
 [4,1,14,8,13,6,2,11,15,12,9,7,3,10,5,0],[15,12,8,2,4,9,1,7,5,11,3,14,10,0,6,13]],
[[15,1,8,14,6,11,3,4,9,7,2,13,12,0,5,10],[3,13,4,7,15,2,8,14,12,0,1,10,6,9,11,5],
 [0,14,7,11,10,4,13,1,5,8,12,6,9,3,2,15],[13,8,10,1,3,15,4,2,11,6,7,12,0,5,14,9]],
[[10,0,9,14,6,3,15,5,1,13,12,7,11,4,2,8],[13,7,0,9,3,4,6,10,2,8,5,14,12,11,15,1],
 [13,6,4,9,8,15,3,0,11,1,2,12,5,10,14,7],[1,10,13,0,6,9,8,7,4,15,14,3,11,5,2,12]],
[[7,13,14,3,0,6,9,10,1,2,8,5,11,12,4,15],[13,8,11,5,6,15,0,3,4,7,2,12,1,10,14,9],
 [10,6,9,0,12,11,7,13,15,1,3,14,5,2,8,4],[3,15,0,6,10,1,13,8,9,4,5,11,12,7,2,14]],
[[2,12,4,1,7,10,11,6,8,5,3,15,13,0,14,9],[14,11,2,12,4,7,13,1,5,0,15,10,3,9,8,6],
 [4,2,1,11,10,13,7,8,15,9,12,5,6,3,0,14],[11,8,12,7,1,14,2,13,6,15,0,9,10,4,5,3]],
[[12,1,10,15,9,2,6,8,0,13,3,4,14,7,5,11],[10,15,4,2,7,12,9,5,6,1,13,14,0,11,3,8],
 [9,14,15,5,2,8,12,3,7,0,4,10,1,13,11,6],[4,3,2,12,9,5,15,10,11,14,1,7,6,0,8,13]],
[[4,11,2,14,15,0,8,13,3,12,9,7,5,10,6,1],[13,0,11,7,4,9,1,10,14,3,5,12,2,15,8,6],
 [1,4,11,13,12,3,7,14,10,15,6,8,0,5,9,2],[6,11,13,8,1,4,10,7,9,5,0,15,14,2,3,12]],
[[13,2,8,4,6,15,11,1,10,9,3,14,5,0,12,7],[1,15,13,8,10,3,7,4,12,5,6,11,0,14,9,2],
 [7,11,4,1,9,12,14,2,0,6,10,13,15,3,5,8],[2,1,14,7,4,10,8,13,15,12,9,0,3,5,6,11]],
]


def _permute_int(val, table, in_bits):
    """Permute an integer's bits MSB-first per a 1-indexed DES table."""
    out = 0
    for pos in table:
        out = (out << 1) | ((val >> (in_bits - pos)) & 1)
    return out


# SP-boxes: 6-bit input -> 32-bit output with P already applied.
_SP = []
for _i in range(8):
    _tbl = []
    for _v in range(64):
        _row = ((_v & 0x20) >> 4) | (_v & 1)
        _col = (_v >> 1) & 0xF
        _nib = _SBOXES[_i][_row][_col]
        _word = _nib << (28 - 4 * _i)          # place nibble in its 32-bit slot
        _tbl.append(_permute_int(_word, _P, 32))
    _SP.append(_tbl)

# E-expansion is regular: group j reads 6 bits starting at (4j-1) mod 32.
_EROT = [(4 * _j + 31) % 32 for _j in range(8)]


def _gen_subkeys(key8):
    """16 x 48-bit round subkeys as integers."""
    k = _permute_int(int.from_bytes(key8, "big"), _PC1, 64)
    c, d = (k >> 28) & 0xFFFFFFF, k & 0xFFFFFFF
    subs = []
    for s in _SHIFTS:
        c = ((c << s) | (c >> (28 - s))) & 0xFFFFFFF
        d = ((d << s) | (d >> (28 - s))) & 0xFFFFFFF
        subs.append(_permute_int((c << 28) | d, _PC2, 56))
    return subs


def _crypt_block(block8, subkeys):
    x = _permute_int(int.from_bytes(block8, "big"), _IP, 64)
    l, r = (x >> 32) & 0xFFFFFFFF, x & 0xFFFFFFFF
    for sk in subkeys:
        # expand R to 48 bits, mix with the subkey, then SP-box it back to 32
        e = 0
        for j in range(8):
            s = _EROT[j]
            rot = ((r << s) | (r >> (32 - s))) & 0xFFFFFFFF
            e = (e << 6) | (rot >> 26)
        e ^= sk
        f = 0
        for j in range(8):
            f |= _SP[j][(e >> (42 - 6 * j)) & 0x3F]
        l, r = r, l ^ f
    return _permute_int((r << 32) | l, _FP, 64).to_bytes(8, "big")


class DES:
    def __init__(self, key8):
        if len(key8) != 8:
            raise ValueError("DES key must be 8 bytes")
        self._enc = _gen_subkeys(key8)
        self._dec = list(reversed(self._enc))

    def encrypt_block(self, b):
        return _crypt_block(b, self._enc)

    def decrypt_block(self, b):
        return _crypt_block(b, self._dec)


class TripleDES:
    """EDE3. Accepts 24-byte (K1,K2,K3) or 16-byte (K1,K2,K1) keys."""
    def __init__(self, key):
        if len(key) == 24:
            k1, k2, k3 = key[:8], key[8:16], key[16:]
        elif len(key) == 16:
            k1, k2, k3 = key[:8], key[8:], key[:8]
        else:
            raise ValueError("TripleDES key must be 16 or 24 bytes")
        d1, d2, d3 = DES(k1), DES(k2), DES(k3)
        # flatten the schedules so a block is 3 calls with no object hops
        self._enc = (d1._enc, d2._dec, d3._enc)
        self._dec = (d3._dec, d2._enc, d1._dec)

    def encrypt_block(self, b):
        a, bb, c = self._enc
        return _crypt_block(_crypt_block(_crypt_block(b, a), bb), c)

    def decrypt_block(self, b):
        a, bb, c = self._dec
        return _crypt_block(_crypt_block(_crypt_block(b, a), bb), c)


def pkcs7_pad(data, bs=8):
    n = bs - (len(data) % bs)
    return data + bytes([n]) * n


def pkcs7_unpad(data, bs=8):
    if not data:
        raise ValueError("empty plaintext")
    n = data[-1]
    if n < 1 or n > bs or data[-n:] != bytes([n]) * n:
        raise ValueError("bad PKCS7 padding")
    return data[:-n]


try:
    import winbcrypt as _native
    if not _native.available():
        _native = None
except Exception:
    _native = None


def backend():
    """'windows-cng' when the native 3DES is in use, else 'pure-python'."""
    return "windows-cng" if _native else "pure-python"


def cbc_decrypt(key, iv, data, unpad=True):
    if len(data) % 8:
        raise ValueError("ciphertext not a multiple of 8 bytes")
    if _native and unpad:
        return _native.cbc_decrypt(key, iv, data)
    c = TripleDES(key)
    dec = c.decrypt_block
    out = bytearray()
    prev = bytes(iv)
    for i in range(0, len(data), 8):
        blk = data[i:i+8]
        d = dec(blk)
        out += bytes(p ^ q for p, q in zip(d, prev))
        prev = blk
    return pkcs7_unpad(bytes(out)) if unpad else bytes(out)


def cbc_encrypt(key, iv, data, pad=True):
    if _native and pad:
        return _native.cbc_encrypt(key, iv, data)
    c = TripleDES(key)
    enc = c.encrypt_block
    if pad:
        data = pkcs7_pad(data)
    out = bytearray()
    prev = bytes(iv)
    for i in range(0, len(data), 8):
        blk = bytes(p ^ q for p, q in zip(data[i:i+8], prev))
        prev = enc(blk)
        out += prev
    return bytes(out)


if __name__ == "__main__":
    import time
    d = DES(bytes.fromhex("133457799BBCDFF1"))
    ct = d.encrypt_block(bytes.fromhex("0123456789ABCDEF"))
    assert ct.hex().upper() == "85E813540F0AB405", ct.hex()
    assert d.decrypt_block(ct) == bytes.fromhex("0123456789ABCDEF")

    d2 = DES(bytes.fromhex("0000000000000000"))
    assert d2.encrypt_block(bytes(8)).hex().upper() == "8CA64DE9C1B123A7"

    t = TripleDES(bytes.fromhex("133457799BBCDFF1") * 3)
    assert t.encrypt_block(bytes.fromhex("0123456789ABCDEF")).hex().upper() == "85E813540F0AB405"

    key = bytes(range(24)); iv = bytes(range(8))
    msg = b"Eiyuden Chronicle save data round-trip test!!"
    assert cbc_decrypt(key, iv, cbc_encrypt(key, iv, msg)) == msg

    blob = bytes(range(256)) * 128        # 32 KB
    t0 = time.time(); ct = cbc_encrypt(key, iv, blob); el = time.time() - t0
    assert cbc_decrypt(key, iv, ct) == blob
    print("DES/3DES self-tests passed (FIPS vectors + CBC round-trip)")
    print(f"backend: {backend()}   32 KB encrypt: {el:.3f}s")

    # The two backends must agree byte-for-byte -- a save written by one has to be
    # readable by the other, and by the game.
    if _native:
        pure_ct = cbc_encrypt(key, iv, pkcs7_pad(blob), pad=False)
        native_ct = _native.cbc_encrypt(key, iv, blob)
        assert pure_ct == native_ct, "backend mismatch on encrypt"
        assert _native.cbc_decrypt(key, iv, pure_ct) == blob
        assert cbc_decrypt(key, iv, native_ct, unpad=True) == blob
        for n in (0, 1, 7, 8, 9, 63, 64, 65):        # padding edge cases
            m = bytes(range(256))[:n]
            assert cbc_encrypt(key, iv, m) == _native.cbc_encrypt(key, iv, m), n
            assert cbc_decrypt(key, iv, _native.cbc_encrypt(key, iv, m)) == m, n
        print("native and pure-Python backends agree byte-for-byte")
