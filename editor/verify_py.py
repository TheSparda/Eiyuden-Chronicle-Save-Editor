"""Prove the pure-Python 3DES reproduces the game's crypto exactly."""
import json, os
import pydes
import testutil

KEY = bytes.fromhex("b3ba76ead29507bad9e68bab87b6e920fe5193bdce92a870")
IV = bytes.fromhex("2f6e9693c9779505")

_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DUMP = os.path.join(_ROOT, "dump")          # sits beside editor/, not in it
SAVE = testutil.pick_save(max_size=60000)      # a small save keeps this quick

print("=== known-answer test vs captured game data ===")
ok = True
for n in range(7):
    fin = os.path.join(DUMP, f"data_enc_{n}_in.bin")
    fout = os.path.join(DUMP, f"data_enc_{n}_out.bin")
    if not (os.path.exists(fin) and os.path.exists(fout)):
        continue
    plain = open(fin, "rb").read()
    cipher = open(fout, "rb").read()
    dec = pydes.cbc_decrypt(KEY, IV, cipher)
    enc = pydes.cbc_encrypt(KEY, IV, plain)
    d_ok, e_ok = dec == plain, enc == cipher
    ok = ok and d_ok and e_ok
    print(f"  pair {n}: decrypt={'OK' if d_ok else 'FAIL'}  encrypt={'OK' if e_ok else 'FAIL'}"
          f"  ({len(plain)} -> {len(cipher)})")

print("\n=== real save file on disk ===")
data = open(SAVE, "rb").read()
pt = pydes.cbc_decrypt(KEY, IV, data)
print("file:", len(data), "bytes -> plaintext:", len(pt), "bytes")

obj = json.loads(pt.decode("utf-8"))
print("JSON parsed OK; top-level keys:")
for k in list(obj)[:20]:
    print("   ", k)

# byte-exact round-trip: re-encrypting must reproduce the original file
re_enc = pydes.cbc_encrypt(KEY, IV, pt)
print("\nround-trip reproduces original file byte-for-byte:", re_enc == data)
print("ALL CHECKS PASSED" if ok and re_enc == data else "SOMETHING FAILED")
