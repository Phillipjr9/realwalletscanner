#!/usr/bin/env python3
"""
make-puzzle.py - build your own solvable Bitcoin puzzle for a local JLP pool.

The public puzzle's remaining ranges are 139 bits and up; a MacBook cannot
finish one in any human timeframe. This generates a puzzle small enough that
the same software - the same collider binary, the same JLP protocol, the same
tame/wild collision - solves it in seconds or minutes. It is a real hunt with a
real finish line.

    python3 ops/make-puzzle.py --bits 40

Outputs:
    puzzle.json   {secret, pubkey, address, range_start, range_end, dp_bits}
    prints the server command to run

Then:
    1. python3 ops/jlp_server.py --pubkey <pk> --range-start <hex> \
       --range-end <hex> --dp-bits <n>
    2. ./collider --pool jlp://127.0.0.1:17403 --worker <your-address> --verbose
    3. The server prints [SOLVED] key = ...

If you want the prize to be real, send a small amount of BTC to the address
first, then sweep it with the recovered key. Only ever do this with an amount
you are willing to lose if the tooling misbehaves.

Sizing guide (expected ~1.15 * 2 * 2^(bits/2) group operations):
    32 bits   ~  100 K ops   seconds
    40 bits   ~    2 M ops   seconds to minutes
    44 bits   ~    6 M ops   minutes
    48 bits   ~   19 M ops   tens of minutes
    56 bits   ~  300 M ops   hours
Choosing bits larger than ~48 on a laptop is not worth it for a test.
"""

import argparse
import hashlib
import json
import secrets
import sys

from jlp_server import N, compress, pt_mul, hash160


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


def p2pkh(pt) -> str:
    return b58check(b"\x00", hash160(pt))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--bits", type=int, default=40,
                    help="key size in bits (32-56 sensible on a laptop)")
    ap.add_argument("--dp-bits", type=int, default=0,
                    help="distinguished-point bits (0 = auto)")
    ap.add_argument("--out", default="puzzle.json")
    args = ap.parse_args()

    if not 8 <= args.bits <= 120:
        sys.exit("--bits must be 8..120")

    r0 = 1 << (args.bits - 1)
    r1 = (1 << args.bits) - 1
    secret = r0 + secrets.randbelow(r1 - r0)
    pt = pt_mul(secret)
    pub33 = compress(pt)
    addr = p2pkh(pt)

    # dp_bits trades DP count against per-DP work. For a toy range, 8-14 keeps
    # the collision search quick without flooding the wire.
    dp_bits = args.dp_bits or min(14, max(8, args.bits // 3))

    data = {
        "secret_hex": f"{secret:064x}",
        "bits": args.bits,
        "pubkey": pub33.hex(),
        "address": addr,
        "range_start": hex(r0),
        "range_end": hex(r1),
        "dp_bits": dp_bits,
    }
    with open(args.out, "w") as fh:
        json.dump(data, fh, indent=2)

    print(f"puzzle written to {args.out}\n")
    print(f"  bits        {args.bits}")
    print(f"  address     {addr}      <- fund this if you want a real prize")
    print(f"  pubkey      {pub33.hex()}")
    print(f"  range       {hex(r0)} .. {hex(r1)}")
    print(f"  dp_bits     {dp_bits}")
    print(f"  secret      kept in {args.out} - do not lose it, do not share it")
    print()
    print("Run the pool:")
    print(f"  python3 ops/jlp_server.py --pubkey {pub33.hex()} \\")
    print(f"      --range-start {hex(r0)} --range-end {hex(r1)} --dp-bits {dp_bits}")
    print()
    print("Then point collider at it:")
    print("  ./collider --pool jlp://127.0.0.1:17403 --worker <your-address> --verbose")
    print()
    print("Verify a result:")
    print(f"  python3 ops/check-solution.py --puzzle {args.out} --key <hex from server>")


if __name__ == "__main__":
    main()
