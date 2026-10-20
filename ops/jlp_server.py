#!/usr/bin/env python3
"""
jlp_server.py - a minimal Collision Protocol (JLP v3) pool server.

Why this exists: the public pool at pool.collisionprotocol.com:17403 has been
refusing connections since 2026-10-19, the reference server repo the docs link
to returns 404, and the client's last commit was 2026-06-15. The wire protocol
is fully published (docs/JLP-PROTOCOL.md, 562 lines with byte offsets), so the
server can be rebuilt from the specification.

Scope: this implements the v3 subset a v1.5.x free client actually uses -
AUTH, WORK_REQ/WORK_ASN, DP_BATCH_V2, DP_ACK, STATS_RSP, PING/PONG, SOLUTION.
It is plaintext (jlp://). It does not implement the v4 CHALLENGE machinery,
maintenance frames, or in-band auto-update.

What this does NOT do: make puzzle #140 solvable. A pool of one machine on a
139-bit range is 38,000 years of work. Use it to run a real pool with your own
machines on a range you can actually finish, or to learn/verify the protocol.

Usage:
    python3 ops/jlp_server.py --listen 0.0.0.0 --port 17403 \\
        --pubkey 02...  --range-start 0x8000000000000 --range-end 0xfffffffffffff \\
        --dp-bits 20

    # client (same machine):
    ./collider --pool jlp://127.0.0.1:17403 --worker bc1q... --verbose

STATUS: framing, AUTH, work assignment, DP validation, and key reconstruction
are unit-tested in ops/test_jlp_server.py. Interoperability with the real
collider binary is UNVERIFIED - it could not be run in the build environment
(no Metal/CUDA, wrong OS). Treat first contact as a debugging session.
"""

import argparse
import hashlib
import os
import secrets
import socket
import socketserver
import struct
import sys
import threading
import time

# ---------------------------------------------------------------------------
# secp256k1 - minimal point arithmetic (no external dependencies)
# ---------------------------------------------------------------------------

P = 2**256 - 2**32 - 977
N = 0xFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141
GX = 0x79BE667EF9DCBBAC55A06295CE870B07029BFCDB2DCE28D959F2815B16F81798
GY = 0x483ADA7726A3C4655DA4FBFC0E1108A8FD17B448A68554199C47D08FFB10D4B8
G = (GX, GY)


def inv(a, m=P):
    return pow(a, m - 2, m)


def pt_add(p, q):
    if p is None:
        return q
    if q is None:
        return p
    if p[0] == q[0]:
        if (p[1] + q[1]) % P == 0:
            return None
        lam = 3 * p[0] * p[0] % P * inv(2 * p[1] % P) % P
    else:
        lam = (q[1] - p[1]) % P * inv((q[0] - p[0]) % P) % P
    x = (lam * lam - p[0] - q[0]) % P
    y = (lam * (p[0] - x) - p[1]) % P
    return (x, y)


def _jac_double(p):
    x, y, z = p
    if z == 0 or y == 0:
        return (0, 1, 0)
    y2 = y * y % P
    s = 4 * x * y2 % P
    m = 3 * x * x % P
    x3 = (m * m - 2 * s) % P
    y3 = (m * (s - x3) - 8 * y2 * y2) % P
    z3 = 2 * y * z % P
    return (x3, y3, z3)


def _jac_add(p, q):
    x1, y1, z1 = p
    x2, y2, z2 = q
    if z1 == 0:
        return q
    if z2 == 0:
        return p
    z1z1 = z1 * z1 % P
    z2z2 = z2 * z2 % P
    u1 = x1 * z2z2 % P
    u2 = x2 * z1z1 % P
    s1 = y1 * z2 % P * z2z2 % P
    s2 = y2 * z1 % P * z1z1 % P
    if u1 == u2:
        if s1 != s2:
            return (0, 1, 0)
        return _jac_double(p)
    h = (u2 - u1) % P
    i = (2 * h) * (2 * h) % P
    j = h * i % P
    r = 2 * (s2 - s1) % P
    v = u1 * i % P
    x3 = (r * r - j - 2 * v) % P
    y3 = (r * (v - x3) - 2 * s1 * j) % P
    z3 = (((z1 + z2) * (z1 + z2) - z1z1 - z2z2) % P) * h % P
    return (x3, y3, z3)


def pt_mul(k, p=G):
    """Scalar multiplication in Jacobian coordinates.

    The original affine double-and-add did a modular inversion for every point
    addition, which made DP verification far too slow for a real GPU client.
    Jacobian arithmetic needs one inversion at the end. k is taken mod N.
    """
    k %= N
    if k == 0 or p is None:
        return None
    out = (0, 1, 0)                 # infinity in Jacobian coordinates
    addend = (p[0] % P, p[1] % P, 1)
    while k:
        if k & 1:
            out = _jac_add(out, addend)
        addend = _jac_double(addend)
        k >>= 1
    x, y, z = out
    if z == 0:
        return None
    zi = inv(z)
    zi2 = zi * zi % P
    return (x * zi2 % P, y * zi2 % P * zi % P)


def decompress(pub33: bytes):
    if len(pub33) != 33 or pub33[0] not in (2, 3):
        raise ValueError("not a compressed secp256k1 pubkey")
    x = int.from_bytes(pub33[1:], "big")
    if x >= P:
        raise ValueError("x out of field")
    y2 = (pow(x, 3, P) + 7) % P
    y = pow(y2, (P + 1) // 4, P)
    if pow(y, 2, P) != y2:
        raise ValueError("point not on curve")
    if (y & 1) != (pub33[0] & 1):
        y = P - y
    return (x, y)


def compress(pt) -> bytes:
    x, y = pt
    return bytes([2 + (y & 1)]) + x.to_bytes(32, "big")


# ---------------------------------------------------------------------------
# RIPEMD-160
#
# hashlib.new("ripemd160") is not dependable: OpenSSL 3.x moved RIPEMD-160
# into its "legacy" provider, so on many builds (notably Homebrew Python on
# macOS) it raises instead of returning a digest. Address derivation needs it,
# so carry a pure-Python implementation and only prefer OpenSSL when it really
# is there.
# ---------------------------------------------------------------------------

_RMD_R = [
    0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
    7, 4, 13, 1, 10, 6, 15, 3, 12, 0, 9, 5, 2, 14, 11, 8,
    3, 10, 14, 4, 9, 15, 8, 1, 2, 7, 0, 6, 13, 11, 5, 12,
    1, 9, 11, 10, 0, 8, 12, 4, 13, 3, 7, 15, 14, 5, 6, 2,
    4, 0, 5, 9, 7, 12, 2, 10, 14, 1, 3, 8, 11, 6, 15, 13,
]
_RMD_RP = [
    5, 14, 7, 0, 9, 2, 11, 4, 13, 6, 15, 8, 1, 10, 3, 12,
    6, 11, 3, 7, 0, 13, 5, 10, 14, 15, 8, 12, 4, 9, 1, 2,
    15, 5, 1, 3, 7, 14, 6, 9, 11, 8, 12, 2, 10, 0, 4, 13,
    8, 6, 4, 1, 3, 11, 15, 0, 5, 12, 2, 13, 9, 7, 10, 14,
    12, 15, 10, 4, 1, 5, 8, 7, 6, 2, 13, 14, 0, 3, 9, 11,
]
_RMD_S = [
    11, 14, 15, 12, 5, 8, 7, 9, 11, 13, 14, 15, 6, 7, 9, 8,
    7, 6, 8, 13, 11, 9, 7, 15, 7, 12, 15, 9, 11, 7, 13, 12,
    11, 13, 6, 7, 14, 9, 13, 15, 14, 8, 13, 6, 5, 12, 7, 5,
    11, 12, 14, 15, 14, 15, 9, 8, 9, 14, 5, 6, 8, 6, 5, 12,
    9, 15, 5, 11, 6, 8, 13, 12, 5, 12, 13, 14, 11, 8, 5, 6,
]
_RMD_SP = [
    8, 9, 9, 11, 13, 15, 15, 5, 7, 7, 8, 11, 14, 14, 12, 6,
    9, 13, 15, 7, 12, 8, 9, 11, 7, 7, 12, 7, 6, 15, 13, 11,
    9, 7, 15, 11, 8, 6, 6, 14, 12, 13, 5, 14, 13, 13, 7, 5,
    15, 5, 8, 11, 14, 14, 6, 14, 6, 9, 12, 9, 12, 5, 15, 8,
    8, 5, 12, 9, 12, 5, 14, 6, 8, 13, 6, 5, 15, 13, 11, 11,
]
_RMD_K = ([0x00000000] * 16 + [0x5A827999] * 16 + [0x6ED9EBA1] * 16 +
          [0x8F1BBCDC] * 16 + [0xA953FD4E] * 16)
_RMD_KP = ([0x50A28BE6] * 16 + [0x5C4DD124] * 16 + [0x6D703EF3] * 16 +
           [0x7A6D76E9] * 16 + [0x00000000] * 16)
_M32 = 0xFFFFFFFF


def _rmd_f(j, x, y, z):
    if j < 16:
        return x ^ y ^ z
    if j < 32:
        return (x & y) | (~x & z)
    if j < 48:
        return (x | ~y) ^ z
    if j < 64:
        return (x & z) | (y & ~z)
    return x ^ (y | ~z)


def _rol(x, n):
    x &= _M32
    return ((x << n) | (x >> (32 - n))) & _M32


def ripemd160_py(msg: bytes) -> bytes:
    h = [0x67452301, 0xEFCDAB89, 0x98BADCFE, 0x10325476, 0xC3D2E1F0]
    ml = len(msg) * 8
    msg = msg + b"\x80"
    msg += b"\x00" * ((56 - len(msg)) % 64)
    msg += ml.to_bytes(8, "little")
    for off in range(0, len(msg), 64):
        x = list(struct.unpack_from("<16I", msg, off))
        a, b, c, d, e = h
        ap, bp, cp, dp, ep = h
        for j in range(80):
            t = _rol(a + _rmd_f(j, b, c, d) + x[_RMD_R[j]] + _RMD_K[j],
                     _RMD_S[j]) + e
            a, e, d, c, b = e, d, _rol(c, 10), b, t & _M32
            t = _rol(ap + _rmd_f(79 - j, bp, cp, dp) + x[_RMD_RP[j]] + _RMD_KP[j],
                     _RMD_SP[j]) + ep
            ap, ep, dp, cp, bp = ep, dp, _rol(cp, 10), bp, t & _M32
        t = (h[1] + c + dp) & _M32
        h[1] = (h[2] + d + ep) & _M32
        h[2] = (h[3] + e + ap) & _M32
        h[3] = (h[4] + a + bp) & _M32
        h[4] = (h[0] + b + cp) & _M32
        h[0] = t
    return b"".join(v.to_bytes(4, "little") for v in h)


try:
    hashlib.new("ripemd160", b"").digest()
    _HAVE_OPENSSL_RIPE = True
except Exception:                                   # noqa: BLE001
    _HAVE_OPENSSL_RIPE = False

# Escape hatch for machines whose OpenSSL lacks the legacy provider, and for
# testing the fallback on machines where it does not.
if os.environ.get("JLP_FORCE_RIPEMD_FALLBACK"):
    _HAVE_OPENSSL_RIPE = False

RIPEMD_FALLBACK_ACTIVE = not _HAVE_OPENSSL_RIPE


def ripemd160(data: bytes) -> bytes:
    if _HAVE_OPENSSL_RIPE:
        try:
            return hashlib.new("ripemd160", data).digest()
        except Exception:                           # noqa: BLE001
            pass
    return ripemd160_py(data)


def hash160(pt) -> bytes:
    pub = compress(pt)
    return ripemd160(hashlib.sha256(pub).digest())


# ---------------------------------------------------------------------------
# Wire format
# ---------------------------------------------------------------------------

MAGIC = b"KANG"
HDR = struct.Struct("<4sBBH")
MAX_MESSAGE_SIZE = 1 << 20

AUTH = 0x01
AUTH_OK = 0x02
AUTH_FAIL = 0x03
WORK_REQ = 0x10
WORK_ASN = 0x11
DP_SUBMIT = 0x20
DP_ACK = 0x21
DP_SUBMIT_V2 = 0x23
DP_BATCH_V2 = 0x24
DP_BATCH_V3 = 0x26
STATS_REQ = 0x30
STATS_RSP = 0x31
SOLUTION = 0x40
PING = 0x50
PONG = 0x51
MSG_ERROR = 0xFF

KANG_BOTH = 0
KANG_TAME = 1
KANG_WILD = 2
DP_TYPE_TAME = 0
DP_TYPE_WILD = 1

AUTH_V2 = struct.Struct("<64s32sQ16s")          # 120 bytes
WORK_ASN_S = struct.Struct("<33s32s32sIQBQQ")   # 126 bytes
DP_V2 = struct.Struct("<QI32s32sBB")            # 78 bytes
DP_V3 = struct.Struct("<QI32s32sBB32sI")        # 114 bytes
STATS_RSP_S = struct.Struct("<QIIffQI")         # 36 bytes

# The shipped v1.5.x client sets PROTOCOL_VERSION = 4 in its generated header
# and hard-rejects any inbound frame whose flags byte != 4 (see its
# read_message: "Protocol version mismatch ... Disconnecting"). So every
# outbound frame must carry 4, regardless of the min(client,4) rule in the doc.
SERVER_VERSION = 4


def pack_frame(msg_type: int, payload: bytes = b"", version: int = SERVER_VERSION) -> bytes:
    if len(payload) > MAX_MESSAGE_SIZE:
        raise ValueError("payload too large")
    return HDR.pack(MAGIC, msg_type, version, len(payload)) + payload


def read_frame(sock):
    hdr = _recv_exact(sock, HDR.size)
    if hdr is None:
        return None
    magic, mtype, flags, size = HDR.unpack(hdr)
    if magic != MAGIC:
        raise ValueError(f"bad magic {magic!r}")
    if size > MAX_MESSAGE_SIZE:
        raise ValueError(f"frame too large: {size}")
    payload = _recv_exact(sock, size) if size else b""
    if payload is None:
        raise ValueError("truncated payload")
    return mtype, flags, payload


def _recv_exact(sock, n):
    buf = b""
    while len(buf) < n:
        chunk = sock.recv(n - len(buf))
        if not chunk:
            return None if not buf else None
        buf += chunk
    return buf


# ---------------------------------------------------------------------------
# DP validation
# ---------------------------------------------------------------------------

def has_leading_zero_bits(x_be: bytes, bits: int) -> bool:
    v = int.from_bytes(x_be, "big")
    return v < (1 << (256 - bits))


def decode_d(d_be: bytes) -> int:
    """Signed 32-byte big-endian distance, sign-extended."""
    v = int.from_bytes(d_be, "big")
    if v >= 1 << 255:
        v -= 1 << 256
    return v


def encode_d(v: int) -> bytes:
    """Encode a signed distance as 32-byte big-endian two's complement."""
    return (v % (1 << 256)).to_bytes(32, "big")


def _x_bytes(pt):
    return None if pt is None else pt[0].to_bytes(32, "big")


def classify_dp(dp, target_point, range_start_int, preferred=None, spec_base=None):
    """Verify a DP and identify which client convention produced it.

    Returns (ok, convention, effective_d_be).

    The shipped client is not internally consistent here:

    * Spec/CUDA convention ("spec"):
          tame: x = (d_t*G).x
          wild: x = ((Q - range_start*G) + d_w*G).x
          key  = range_start + d_t - d_w

    * Metal/CPU convention ("metal", also the standalone solvers):
          tame: x = (d_t*G).x, with d_t an absolute scalar
          wild: x = (Q + d_w*G).x
          key  = d_t - d_w

    A tame DP is just a point with a known discrete log in both conventions,
    so the convention is determined by the wild DP's base point. For wild DPs
    we also accept the negated-distance form and normalize it into
    effective_d_be, so reconstruction always uses the "+" form above.
    """
    x_be, d_be, dp_type = dp["x"], dp["d"], dp["type"]
    d = decode_d(d_be)

    if dp_type == DP_TYPE_TAME:
        return _x_bytes(pt_mul(d)) == x_be, None, d_be
    if dp_type != DP_TYPE_WILD:
        return False, None, d_be

    conventions = ["spec", "metal"]
    if preferred == "metal":
        conventions.reverse()

    def base_for(convention):
        if convention == "metal":
            return target_point
        if spec_base is not None:
            return spec_base
        return pt_add(target_point, pt_mul(-range_start_int))

    for convention in conventions:
        base = base_for(convention)
        if base is None:
            continue
        if _x_bytes(pt_add(base, pt_mul(d))) == x_be:
            return True, convention, d_be
        if _x_bytes(pt_add(base, pt_mul(-d))) == x_be:
            return True, convention, encode_d(-d)
    return False, None, d_be


def verify_dp(dp, target_point, range_start_int, kang_type=None):
    """Back-compatible boolean DP check (see classify_dp for conventions)."""
    ok, _convention, _effective_d = classify_dp(dp, target_point, range_start_int)
    return ok


def reconstruct_key(dp_tame, dp_wild, range_start_int, convention="spec"):
    """Recover the key from a tame/wild collision.

    spec:  key = range_start + d_tame - d_wild  (mod N)
    metal: key = d_tame - d_wild                (mod N)
    """
    dt = decode_d(dp_tame["d"])
    dw = decode_d(dp_wild["d"])
    if convention == "metal":
        return (dt - dw) % N
    return (range_start_int + dt - dw) % N


# ---------------------------------------------------------------------------
# Server
# ---------------------------------------------------------------------------

class PoolState:
    def __init__(self, target_pub33, range_start, range_end, dp_bits,
                 seq_window=1024, assignment_mode="alternate"):
        if assignment_mode not in ("alternate", "both", "tame", "wild"):
            raise ValueError(f"unknown assignment_mode {assignment_mode!r}")
        self.target = decompress(target_pub33)
        self.spec_base = pt_add(self.target, pt_mul(-range_start))
        self.pub33 = target_pub33
        self.range_start = range_start
        self.range_end = range_end
        self.dp_bits = dp_bits
        self.seq_window = seq_window
        self.assignment_mode = assignment_mode
        self.lock = threading.Lock()
        self.workers = {}          # worker_name -> stats
        self.assignments = {}      # worker_name -> dict
        self.dp_seq_high = {}      # (worker_name, work_id) -> highest seq accepted
        self.tame = {}             # x_be -> dp
        self.wild = {}             # x_be -> dp (with convention + effective d)
        self.last_reject_counts = {}
        self.last_accept_types = {"tame": 0, "wild": 0}
        self.verify_samples = []   # first few verify-failed DPs, for diagnosis
        self.verify_samples_printed = 0
        self.total_dps = 0
        self.started = time.time()
        self.solution = None
        self.solution_announced = False
        self.next_work_id = 1
        self._timestamps = []
        self.puzzle_path = None
        self.solved_path = "solved.txt"

    def assign(self, worker):
        with self.lock:
            wid = self.next_work_id
            self.next_work_id += 1
            if self.assignment_mode == "both":
                # Mac/CPU compatibility mode. The shipped Metal and CPU
                # backends ignore kangaroo_type and always seed both herds;
                # assigning TAME_ONLY/WILD_ONLY makes the pool client drop
                # the other herd locally. kangaroo_type=0 disables that
                # client-side filter. (The CUDA backend rejects 0, so CUDA
                # workers should use --assignment alternate.)
                ktype = KANG_BOTH
            elif self.assignment_mode == "tame":
                ktype = KANG_TAME
            elif self.assignment_mode == "wild":
                ktype = KANG_WILD
            else:
                # Spec/CUDA mode: alternate tame/wild across assignments. A
                # real pool balances across connections; a single reconnecting
                # worker still makes both halves over time.
                ktype = KANG_TAME if wid % 2 == 1 else KANG_WILD
            asn = {
                "work_id": wid,
                "kangaroo_type": ktype,
                "start_offset_a": 0,
                "start_offset_b": self.range_end - self.range_start,
            }
            self.assignments[worker] = asn
            return asn

    def accept_batch(self, worker, payload, v3=False):
        """Returns (accepted_count, error_or_None).

        v2 records are 78 B, v3 records 114 B. A v4 client emits both:
        DP_BATCH_V2 for ordinary DPs and DP_BATCH_V3 for checkpoint-committed
        ones. The extra v3 fields (ckpt_root, n_segments) are only used by the
        optional CHALLENGE verifier, which this server leaves off, so they are
        parsed and ignored.
        """
        rec = DP_V3 if v3 else DP_V2
        if len(payload) < 4:
            return 0, "short batch"
        count = struct.unpack_from("<I", payload, 0)[0]
        if count > 10000:
            return 0, "batch too large"
        if len(payload) < 4 + count * rec.size:
            return 0, "truncated batch"
        asn = self.assignments.get(worker)
        if not asn:
            return 0, "no assignment"

        accepted = 0
        accept_types = {"tame": 0, "wild": 0}
        rejects = {}

        def reject(reason):
            rejects[reason] = rejects.get(reason, 0) + 1

        with self.lock:
            # Sequence high-water is per (worker, work_id): the client resets
            # its counter to 0 on every new assignment, so carrying one mark
            # across work_ids rejects a fresh chunk as a replay.
            seq_key = (worker, asn["work_id"])
            high = self.dp_seq_high.get(seq_key, -1)
            for i in range(count):
                off = 4 + i * rec.size
                if v3:
                    (work_id, seq, x_be, d_be, dp_type, dp_bits,
                     _ckpt_root, _n_seg) = DP_V3.unpack_from(payload, off)
                else:
                    work_id, seq, x_be, d_be, dp_type, dp_bits = \
                        DP_V2.unpack_from(payload, off)

                if work_id != asn["work_id"]:
                    reject("stale_work_id")
                    continue
                if seq <= high - self.seq_window:
                    reject("seq_out_of_window")
                    continue
                if dp_bits != self.dp_bits:
                    reject("dp_bits")
                    continue
                if not has_leading_zero_bits(x_be, dp_bits):
                    reject("not_distinguished")
                    continue
                if dp_type not in (DP_TYPE_TAME, DP_TYPE_WILD):
                    reject("bad_type")
                    continue
                assigned = asn["kangaroo_type"]
                if assigned == KANG_TAME and dp_type != DP_TYPE_TAME:
                    reject("wrong_type")
                    continue
                if assigned == KANG_WILD and dp_type != DP_TYPE_WILD:
                    reject("wrong_type")
                    continue

                preferred = "metal" if assigned == KANG_BOTH else "spec"
                ok, convention, effective_d = classify_dp(
                    {"x": x_be, "d": d_be, "type": dp_type},
                    self.target, self.range_start, preferred=preferred,
                    spec_base=self.spec_base)
                if not ok:
                    reject("verify_tame" if dp_type == DP_TYPE_TAME
                           else "verify_wild")
                    if len(self.verify_samples) < 8:
                        self.verify_samples.append(
                            {"type": dp_type, "x": x_be.hex(),
                             "d": d_be.hex(), "seq": seq})
                    continue

                high = max(high, seq)
                dp = {
                    "x": x_be,
                    "d": effective_d,
                    "raw_d": d_be,
                    "type": dp_type,
                    "work_id": work_id,
                    "worker": worker,
                    "convention": convention,
                }
                accepted += 1
                self.total_dps += 1
                accept_types["tame" if dp_type == DP_TYPE_TAME else "wild"] += 1

                if dp_type == DP_TYPE_TAME:
                    self.tame[x_be] = dp
                    hit = self.wild.get(x_be)
                    if hit and self.solution is None:
                        k = reconstruct_key(dp, hit, self.range_start,
                                            hit.get("convention") or "spec")
                        if pt_mul(k) == self.target:
                            self.solution = {
                                "key": k,
                                "worker_tame": worker,
                                "worker_wild": hit.get("worker"),
                            }
                else:
                    self.wild[x_be] = dp
                    hit = self.tame.get(x_be)
                    if hit and self.solution is None:
                        k = reconstruct_key(hit, dp, self.range_start,
                                            convention or "spec")
                        if pt_mul(k) == self.target:
                            self.solution = {
                                "key": k,
                                "worker_tame": hit.get("worker"),
                                "worker_wild": worker,
                            }

            self.last_reject_counts = rejects
            self.last_accept_types = accept_types
            self.dp_seq_high[seq_key] = high
            w = self.workers.setdefault(worker, {"dps": 0, "last": 0, "dp_seq_high": -1})
            w["dps"] += accepted
            w["last"] = time.time()
            w["dp_seq_high"] = high
            self._timestamps.extend([time.time()] * accepted)
            self._timestamps = self._timestamps[-256:]

        return accepted, None

    def stats(self, worker):
        with self.lock:
            now = time.time()
            recent = [t for t in self._timestamps if now - t <= 60]
            rate = len(recent) / 60.0
            mine = self.workers.get(worker, {}).get("dps", 0)
            share = (mine / self.total_dps) if self.total_dps else 0.0
            return {
                "total_dps": self.total_dps,
                "total_workers": len(self.workers),
                "active_workers": sum(1 for w in self.workers.values()
                                      if now - w["last"] <= 300),
                "dps_per_second": rate,
                "your_share": share,
                "your_dps": mine,
                "uptime": int(now - self.started),
            }


class Handler(socketserver.BaseRequestHandler):
    def handle(self):
        srv = self.server
        state = srv.state
        sock = self.request
        sock.settimeout(300)
        peer = self.client_address
        worker = None
        try:
            # ---- AUTH ---------------------------------------------------
            frame = read_frame(sock)
            if frame is None:
                return
            mtype, flags, payload = frame
            if mtype != AUTH:
                sock.sendall(pack_frame(MSG_ERROR, b"expected AUTH"))
                return
            if flags < 3:
                sock.sendall(pack_frame(AUTH_FAIL, b"protocol_version_mismatch"))
                return
            if len(payload) < AUTH_V2.size:
                sock.sendall(pack_frame(AUTH_FAIL, b"bad AUTH length"))
                return
            name_b, _pw, ts_ms, _nonce = AUTH_V2.unpack(payload[:AUTH_V2.size])
            name = name_b.split(b"\x00", 1)[0].decode("utf-8", "replace")
            # The client stamps flags=4 even when it sends the unsigned
            # 120-byte body, so the version byte alone does not tell us which
            # body shape arrived. Size does: a signed v4 hello is longer and
            # still starts with the same 120-byte prefix.
            if abs(ts_ms / 1000.0 - time.time()) > 30:
                sock.sendall(pack_frame(AUTH_FAIL, b"auth_clock_skew"))
                return
            if not name:
                sock.sendall(pack_frame(AUTH_FAIL, b"empty worker_name"))
                return
            worker = name
            sock.sendall(pack_frame(AUTH_OK, b""))
            print(f"[auth] {worker} from {peer[0]} (client v{flags})", flush=True)

            # ---- main loop ----------------------------------------------
            while True:
                frame = read_frame(sock)
                if frame is None:
                    return
                mtype, flags, payload = frame

                if mtype == WORK_REQ:
                    asn = state.assign(worker)
                    body = WORK_ASN_S.pack(
                        state.pub33,
                        state.range_start.to_bytes(32, "big"),
                        state.range_end.to_bytes(32, "big"),
                        state.dp_bits,
                        asn["work_id"],
                        asn["kangaroo_type"],
                        asn["start_offset_a"],
                        asn["start_offset_b"],
                    )
                    sock.sendall(pack_frame(WORK_ASN, body))
                    type_name = {KANG_BOTH: "BOTH", KANG_TAME: "TAME",
                                 KANG_WILD: "WILD"}.get(asn["kangaroo_type"],
                                                         str(asn["kangaroo_type"]))
                    print(f"[work] {worker} work_id={asn['work_id']} "
                          f"type={type_name}", flush=True)

                elif mtype in (DP_BATCH_V2, DP_BATCH_V3):
                    n, err = state.accept_batch(worker, payload,
                                                v3=(mtype == DP_BATCH_V3))
                    sock.sendall(pack_frame(DP_ACK, struct.pack("<I", n)))
                    if n:
                        at = state.last_accept_types
                        print(f"[dp] {worker} accepted {n} "
                              f"(tame={at['tame']} wild={at['wild']}) "
                              f"stored tame={len(state.tame)} "
                              f"wild={len(state.wild)}", flush=True)
                        if state.last_reject_counts:
                            print(f"[dp] {worker} also rejected "
                                  f"{state.last_reject_counts}", flush=True)
                    elif state.last_reject_counts:
                        print(f"[dp] {worker} accepted 0 "
                              f"rejects={state.last_reject_counts}", flush=True)
                    while state.verify_samples_printed < len(state.verify_samples):
                        s = state.verify_samples[state.verify_samples_printed]
                        state.verify_samples_printed += 1
                        print(f"[dp] verify-fail sample type={s['type']} "
                              f"seq={s['seq']} x={s['x']} d={s['d']}", flush=True)
                    if err:
                        print(f"[dp] {worker} rejected: {err}", flush=True)
                    if state.solution:
                        sol = state.solution
                        body = sol["key"].to_bytes(32, "big")
                        sock.sendall(pack_frame(SOLUTION, body))
                        print(f"[SOLVED] key = {sol['key']:064x}", flush=True)
                        if not state.solution_announced:
                            state.solution_announced = True
                            print(f"[SOLVED] verify: python3 ops/check-solution.py "
                                  f"--puzzle {state.puzzle_path} "
                                  f"--key {sol['key']:064x}", flush=True)
                            if state.puzzle_path:
                                try:
                                    with open(state.solved_path, "w") as _fh:
                                        _fh.write(f"{sol['key']:064x}\n")
                                    print(f"[SOLVED] written to {state.solved_path}",
                                          flush=True)
                                except OSError as _e:
                                    print(f"[SOLVED] could not write: {_e}", flush=True)

                elif mtype in (DP_SUBMIT, DP_SUBMIT_V2):
                    # Deprecated single-DP paths; wrap as a 1-record batch.
                    rec = DP_V2.size
                    if len(payload) < rec:
                        sock.sendall(pack_frame(MSG_ERROR, b"short DP"))
                        continue
                    n, _ = state.accept_batch(worker,
                                              struct.pack("<I", 1) + payload[:rec])
                    sock.sendall(pack_frame(DP_ACK, struct.pack("<I", n)))

                elif mtype == STATS_REQ:
                    st = state.stats(worker)
                    body = STATS_RSP_S.pack(
                        st["total_dps"], st["total_workers"], st["active_workers"],
                        st["dps_per_second"], st["your_share"],
                        st["your_dps"], st["uptime"],
                    )
                    sock.sendall(pack_frame(STATS_RSP, body))

                elif mtype == PING:
                    sock.sendall(pack_frame(PONG, b""))

                else:
                    sock.sendall(pack_frame(MSG_ERROR, b"unsupported type"))

        except (ConnectionResetError, BrokenPipeError, socket.timeout):
            pass
        except Exception as exc:                      # noqa: BLE001
            print(f"[err] {worker or peer}: {type(exc).__name__}: {exc}", flush=True)
        finally:
            try:
                sock.close()
            except OSError:
                pass
            if worker:
                print(f"[bye] {worker}", flush=True)


class Server(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True

    def __init__(self, addr, state):
        self.state = state
        super().__init__(addr, Handler)


def _check_known_answer():
    """Prove the DP->key reconstruction on a key we know, before trusting it."""
    import secrets as _s
    r0 = 1 << 39
    secret = r0 + _s.randbelow(1 << 20)
    Q = pt_mul(secret)
    print(f"  known secret     {secret:064x}")
    # build a genuine collision: pick d_wild, derive the matching tame point
    dw = _s.randbelow(1 << 20)
    tame_pt = pt_add(Q, pt_mul(-r0))
    tame_pt = pt_add(tame_pt, pt_mul(dw))
    # By construction the tame distance is (secret - r0) + dw, which is exactly
    # what the server must recover from the collision alone.
    dt = (secret - r0) + dw
    print(f"  d_tame           {dt}")
    print(f"  d_wild           {dw}")
    if pt_mul(dt) != tame_pt:
        print("  \033[31mFAIL\033[0m construction mismatch")
        return 1
    k = reconstruct_key({"d": dt.to_bytes(32, "big")},
                        {"d": dw.to_bytes(32, "big")}, r0)
    ok = k == secret and pt_mul(k) == Q
    print(f"  reconstructed    {k:064x}")
    print("  \033[32mMATCH\033[0m" if ok else "  \033[31mMISMATCH\033[0m")
    return 0 if ok else 1


def main(argv=None):
    ap = argparse.ArgumentParser(description="Minimal JLP v3 pool server")
    ap.add_argument("--listen", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=17403)
    ap.add_argument("--pubkey",
                    help="33-byte compressed target pubkey (hex); required "
                         "unless --check-known-answer")
    ap.add_argument("--range-start", help="hex, e.g. 0x8000...")
    ap.add_argument("--range-end", help="hex")
    ap.add_argument("--dp-bits", type=int, default=20)
    ap.add_argument("--assignment", choices=("alternate", "both", "tame", "wild"),
                    default="alternate",
                    help="work assignment policy: alternate = spec/CUDA "
                         "TAME_ONLY/WILD_ONLY rotation; both = macOS Metal/CPU "
                         "compatibility (kangaroo_type=0, disables the client's "
                         "one-sided DP filter; CUDA clients reject this)")
    ap.add_argument("--puzzle", help="puzzle.json from make-puzzle.py; also writes "
                                     "solved.txt on success")
    ap.add_argument("--solved", default="solved.txt")
    ap.add_argument("--check-known-answer", action="store_true",
                    help="offline self-test: prove the key math on a known key")
    args = ap.parse_args(argv)

    if args.check_known_answer:
        return _check_known_answer()

    if not (args.pubkey and args.range_start and args.range_end):
        ap.error("--pubkey, --range-start and --range-end are required")
    pub33 = bytes.fromhex(args.pubkey.removeprefix("0x"))
    if len(pub33) != 33:
        sys.exit("--pubkey must be 33 bytes compressed")
    r0 = int(args.range_start, 16)
    r1 = int(args.range_end, 16)
    if r1 <= r0:
        sys.exit("--range-end must exceed --range-start")
    if not 8 <= args.dp_bits <= 32:
        sys.exit("--dp-bits must be 8..32")

    state = PoolState(pub33, r0, r1, args.dp_bits,
                      assignment_mode=args.assignment)
    state.puzzle_path = args.puzzle
    state.solved_path = args.solved
    server = Server((args.listen, args.port), state)
    print(f"JLP v3 test pool on {args.listen}:{args.port}")
    print(f"  target   {pub33.hex()}")
    print(f"  range    {args.range_start} .. {args.range_end}")
    print(f"  dp_bits  {args.dp_bits}")
    print(f"  assign   {args.assignment}")
    if args.assignment == "both":
        print("  note     BOTH is for macOS Metal/CPU clients; CUDA clients "
              "reject kangaroo_type=0 (use --assignment alternate for CUDA)")
    if args.puzzle:
        print(f"  puzzle   {args.puzzle}")
    print(f"  client   ./collider --pool jlp://127.0.0.1:{args.port} "
          f"--worker <addr> --verbose")
    print("  Ctrl+C to stop\n", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("\nstopping")
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
