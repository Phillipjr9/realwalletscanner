#!/usr/bin/env python3
"""
validate-worker.py - offline sanity check for a Collision Protocol payout address.

Why this exists
---------------
The JLP v3 pool server does NOT validate the --worker address. The docs are
explicit: "The server does not validate the address format (so a typo will not
bounce AUTH); it just uses the string as a key." The client only checks the
charset ([A-Za-z0-9_.-], 1..64 chars). So a one-character typo accepts fine,
accrues DPs under a dead bucket, and your share is unrecoverable.

This script checks the actual Bitcoin checksum offline (no network, no keys,
nothing leaves the machine) for:
  - Base58Check  : 1... (P2PKH) and 3... (P2SH)
  - Bech32       : bc1q...  (P2WPKH / P2WSH, witness v0)
  - Bech32m      : bc1p...  (P2TR, witness v1)

It proves the string is a well-formed mainnet address. It does NOT prove you
control it - only a spend round-trip does that. See HUNT-OPS.md.

Usage:
    python3 ops/validate-worker.py 36vjpBDd2yT6XwuBJPXgr7ksPBxhyCk4aP
    python3 ops/validate-worker.py --quiet <addr>   # exit code only

Exit codes: 0 = valid mainnet address, 1 = invalid, 2 = usage error.
"""

import sys

B58_ALPHABET = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz"
BECH32_CHARSET = "qpzry9x8gf2tvdw0s3jn54khce6mua7l"
BECH32M_CONST = 0x2BC830A3


def _sha256(b: bytes) -> bytes:
    import hashlib

    return hashlib.sha256(b).digest()


def base58check_decode(addr: str) -> bytes:
    """Return the payload (version + hash) or raise ValueError."""
    num = 0
    for ch in addr:
        idx = B58_ALPHABET.find(ch)
        if idx < 0:
            raise ValueError(f"character {ch!r} is not in the base58 alphabet")
        num = num * 58 + idx

    body = num.to_bytes((num.bit_length() + 7) // 8, "big") if num else b""
    # Leading '1's are leading zero bytes and are dropped by the int conversion.
    pad = len(addr) - len(addr.lstrip("1"))
    raw = b"\x00" * pad + body

    if len(raw) < 5:
        raise ValueError("too short to contain a checksum")
    payload, checksum = raw[:-4], raw[-4:]
    if _sha256(_sha256(payload))[:4] != checksum:
        raise ValueError("checksum mismatch (typo or truncated address)")
    return payload


def _bech32_polymod(values) -> int:
    generator = (0x3B6A57B2, 0x26508E6D, 0x1EA119FA, 0x3D4233DD, 0x2A1462B3)
    chk = 1
    for value in values:
        top = chk >> 25
        chk = (chk & 0x1FFFFFF) << 5 ^ value
        for i in range(5):
            chk ^= generator[i] if ((top >> i) & 1) else 0
    return chk


def _bech32_hrp_expand(hrp: str):
    return [ord(x) >> 5 for x in hrp] + [0] + [ord(x) & 31 for x in hrp]


def bech32_decode(addr: str):
    """Return (hrp, data_ints, spec) where spec is 'bech32' or 'bech32m'."""
    if addr.lower() != addr and addr.upper() != addr:
        raise ValueError("mixed case is not allowed in bech32")
    addr = addr.lower()
    pos = addr.rfind("1")
    if pos < 1 or pos + 7 > len(addr):
        raise ValueError("missing or misplaced bech32 separator '1'")
    hrp, data_part = addr[:pos], addr[pos + 1:]
    if hrp != "bc":
        raise ValueError(f"not a Bitcoin mainnet address (hrp={hrp!r})")

    data = []
    for ch in data_part:
        idx = BECH32_CHARSET.find(ch)
        if idx < 0:
            raise ValueError(f"character {ch!r} is not in the bech32 charset")
        data.append(idx)

    const = _bech32_polymod(_bech32_hrp_expand(hrp) + data)
    if const == 1:
        spec = "bech32"
    elif const == BECH32M_CONST:
        spec = "bech32m"
    else:
        raise ValueError("bech32 checksum failure (typo or truncated address)")
    return hrp, data[:-6], spec


def _convertbits(data, frombits: int, tobits: int, pad: bool = False):
    acc = 0
    bits = 0
    ret = []
    maxv = (1 << tobits) - 1
    for value in data:
        if value < 0 or (value >> frombits):
            return None
        acc = (acc << frombits) | value
        bits += frombits
        while bits >= tobits:
            bits -= tobits
            ret.append((acc >> bits) & maxv)
    if pad:
        if bits:
            ret.append((acc << (tobits - bits)) & maxv)
    elif bits >= frombits or ((acc << (tobits - bits)) & maxv):
        return None
    return ret


PLACEHOLDERS = (
    "1yourfreshbitcoinaddress",
    "1yourbitcoinaddressforrewards",
    "1yourbitcoinaddress",
    "yourbitcoinaddresshere",
    "bc1qyourbtcaddress",
)


def classify(addr: str):
    """Return (kind, detail) or raise ValueError with a human reason."""
    addr = addr.strip()
    if not addr:
        raise ValueError("empty address")
    if addr.lower() in PLACEHOLDERS or "yourbitcoinaddress" in addr.lower() \
            or "freshbitcoinaddress" in addr.lower():
        raise ValueError(
            "this is a PLACEHOLDER copied from a sample config, not a wallet.\n"
            "      Every DP sent under it is credited to a bucket nobody owns.\n"
            "      Replace it with an address from a wallet whose seed you hold."
        )
    if len(addr) > 64:
        raise ValueError("longer than the 64-byte worker_name field on the wire")

    if addr[0] in "13":
        payload = base58check_decode(addr)
        version, h160 = payload[0], payload[1:]
        if len(h160) != 20:
            raise ValueError(f"unexpected hash length {len(h160)} (expected 20)")
        kind = "P2PKH" if version == 0x00 else "P2SH" if version == 0x05 else None
        if kind is None:
            raise ValueError(f"not a mainnet address (version byte 0x{version:02x})")
        if kind == "P2PKH" and not addr.startswith("1"):
            raise ValueError("version byte and prefix disagree")
        if kind == "P2SH" and not addr.startswith("3"):
            raise ValueError("version byte and prefix disagree")
        return kind, f"{kind} mainnet, 20-byte hash160 = {h160.hex()}"

    if addr[:3].lower() in ("bc1", "tb1", "bc1"):
        _, data, spec = bech32_decode(addr)
        if not data:
            raise ValueError("empty witness payload")
        version = data[0]
        program = _convertbits(data[1:], 5, 8, False)
        if program is None:
            raise ValueError("invalid witness program encoding")
        if version > 16:
            raise ValueError(f"witness version {version} out of range")
        if version == 0:
            if spec != "bech32":
                raise ValueError("witness v0 must use bech32, not bech32m")
            if len(program) not in (20, 32):
                raise ValueError("witness v0 program must be 20 or 32 bytes")
            kind = "P2WPKH" if len(program) == 20 else "P2WSH"
        else:
            if spec != "bech32m":
                raise ValueError(f"witness v{version} must use bech32m")
            if not 2 <= len(program) <= 40:
                raise ValueError("witness program must be 2..40 bytes")
            kind = "P2TR" if version == 1 and len(program) == 32 else f"P2WSH-v{version}"
        return kind, f"{kind} mainnet, witness program {len(program)} bytes"

    raise ValueError("unrecognised address format (expected 1..., 3..., bc1q..., bc1p...)")


def main(argv) -> int:
    quiet = "--quiet" in argv
    args = [a for a in argv[1:] if a != "--quiet"]
    if len(args) != 1:
        print(__doc__.strip().splitlines()[0], file=sys.stderr)
        print("usage: python3 ops/validate-worker.py [--quiet] <btc-address>", file=sys.stderr)
        return 2

    addr = args[0]
    try:
        kind, detail = classify(addr)
    except ValueError as exc:
        if not quiet:
            print(f"FAIL  {addr}")
            print(f"      {exc}")
            print()
            print("Do NOT use this string as --worker. DPs accrued under it are")
            print("unrecoverable. The pool would accept it silently.")
        return 1

    if not quiet:
        print(f"OK    {addr}")
        print(f"      {detail}")
        print()
        print("Well-formed. Now prove you can SPEND from it:")
        print("  send a few thousand sats in, then send them back out to another")
        print("  address you control. If that round-trip confirms, you own it.")
        print()
        if kind == "P2SH":
            print("NOTE  P2SH addresses are often exchange/multisig deposit addresses.")
            print("      If this came from an exchange UI, it is NOT safe to use here.")
            print("      Payout must land in a wallet whose seed you physically hold.")
        else:
            print("NOTE  This address is public on the pool dashboard. That is fine -")
            print("      it cannot be spent from without your key. Keep the seed offline.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
