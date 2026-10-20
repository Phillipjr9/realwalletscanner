#!/usr/bin/env python3
"""
check-solution.py - verify a key the local pool recovered actually owns the
puzzle address, without trusting anything the server printed.

    python3 ops/check-solution.py --puzzle puzzle.json --key <hex>
    python3 ops/check-solution.py --puzzle puzzle.json --key-file solved.txt

Recomputes the public key from the private key, derives the P2PKH address, and
compares it to the puzzle's address. Exit 0 = match.
"""

import argparse
import hashlib
import json
import sys

from jlp_server import compress, hash160, pt_mul


def b58check(prefix: bytes, payload: bytes) -> str:
    alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz"
    raw = prefix + payload
    chk = hashlib.sha256(hashlib.sha256(raw).digest()).digest()[:4]
    data = raw + chk
    num = int.from_bytes(data, "big")
    out = ""
    while num:
        num, rem = divmod(num, 58)
        out = alphabet[rem] + out
    pad = 0
    for b in data:
        if b == 0:
            pad += 1
        else:
            break
    return "1" * pad + out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--puzzle", default="puzzle.json")
    ap.add_argument("--key", help="recovered private key, hex")
    ap.add_argument("--key-file", help="file containing the key hex")
    args = ap.parse_args()

    with open(args.puzzle) as fh:
        p = json.load(fh)

    key_hex = args.key
    if not key_hex and args.key_file:
        with open(args.key_file) as fh:
            key_hex = fh.read().strip().split()[-1]
    if not key_hex:
        sys.exit("--key or --key-file is required")

    k = int(key_hex.removeprefix("0x"), 16)
    pt = pt_mul(k)
    pub = compress(pt).hex()
    addr = b58check(b"\x00", hash160(pt))

    ok_pub = pub == p["pubkey"]
    ok_addr = addr == p["address"]
    in_range = int(p["range_start"], 16) <= k <= int(p["range_end"], 16)

    print(f"  key            {k:064x}")
    print(f"  derives pubkey {pub}")
    print(f"  expected       {p['pubkey']}   {'MATCH' if ok_pub else 'MISMATCH'}")
    print(f"  derives address {addr}")
    print(f"  expected        {p['address']}   {'MATCH' if ok_addr else 'MISMATCH'}")
    print(f"  in puzzle range {'yes' if in_range else 'NO'}")

    if ok_pub and ok_addr and in_range:
        print("\n  \033[32mVERIFIED\033[0m - this key controls the puzzle address.")
        return 0
    print("\n  \033[31mREJECTED\033[0m - the key does not match the puzzle.")
    return 1


if __name__ == "__main__":
    sys.exit(main())
