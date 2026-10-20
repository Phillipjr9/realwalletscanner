#!/usr/bin/env python3
"""
test_jlp_server.py - proves the JLP v3 server works, without a GPU.

It builds a toy puzzle whose private key is inside a 20-bit range, then drives
the server over a real TCP socket exactly as a client would: AUTH, WORK_REQ
(tame), submit a tame DP, WORK_REQ (wild), submit a wild DP that collides with
it, and check the server emits SOLUTION carrying the correct private key.

Run:  python3 ops/test_jlp_server.py
Exit: 0 all tests pass, 1 failure.
"""

import hashlib
import socket
import struct
import sys
import threading
import time

import jlp_server as J

FAILS = []


def check(name, cond, detail=""):
    if cond:
        print(f"  \033[32mPASS\033[0m {name}")
    else:
        print(f"  \033[31mFAIL\033[0m {name} {detail}")
        FAILS.append(name)


# ---------------------------------------------------------------------------
def test_curve():
    print("\n[curve]")
    check("G is on the curve", (J.GY * J.GY - (J.GX ** 3 + 7)) % J.P == 0)
    q = J.pt_mul(2)
    check("2G == G+G", q == J.pt_add(J.G, J.G))
    check("(n-1)G == -G", J.pt_mul(J.N - 1) == (J.GX, (-J.GY) % J.P))
    # Known test vector: k=1 -> compressed G
    check("compress(G) matches known prefix",
          J.compress(J.G).hex().startswith("0279be667e"))
    k = 0xDEADBEEF
    check("decompress(compress(kG)) round-trips",
          J.decompress(J.compress(J.pt_mul(k))) == J.pt_mul(k))


def test_ripemd():
    """Address derivation must not depend on OpenSSL having legacy algorithms."""
    print("\n[ripemd-160]")
    vectors = [
        (b"", "9c1185a5c5e9fc54612808977ee8f548b2258d31"),
        (b"abc", "8eb208f7e05d987a9b044a8e98c6b087f15a0bfc"),
        (b"message digest", "5d0689ef49d2fae572b881b123a85ffa21595f36"),
        (b"abcdefghijklmnopqrstuvwxyz",
         "f71c27109c692c1b56bbdceb5b9d2865b3708dbc"),
        (b"abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq",
         "12a053384a9c0c88e405a06c27dcf49ada62eb2b"),
    ]
    ok = True
    for msg, want in vectors:
        got = J.ripemd160_py(msg).hex()
        if got != want:
            ok = False
            check(f"pure-python ripemd160({msg[:16]!r})", False,
                  f"got {got} want {want}")
    check("pure-python RIPEMD-160 matches all 5 official vectors", ok)
    # hash160 of Satoshi's genesis coinbase pubkey
    pub = bytes.fromhex(
        "04678afdb0fe5548271967f1a67130b7105cd6a828e03909a67962e0ea1f61deb6"
        "49f6bc3f4cef38c4f35504e51ec112de5c384df7ba0b8d578a4c702b6bf11d5f")
    h = J.ripemd160_py(hashlib.sha256(pub).digest()).hex()
    check("hash160 via fallback matches the known genesis value",
          h == "62e907b15cbf27d5425399ebf6f0fb50ebb88f18", h)
    if J.RIPEMD_FALLBACK_ACTIVE:
        print("  (this build is using the fallback)")
    else:
        check("OpenSSL and fallback agree",
              J.ripemd160_py(b"check") == J.ripemd160(b"check"))


def test_framing():
    print("\n[framing]")
    f = J.pack_frame(J.WORK_ASN, b"\x00" * J.WORK_ASN_S.size)
    check("header is 8 bytes", len(f) - J.WORK_ASN_S.size == 8)
    magic, mtype, ver, size = J.HDR.unpack(f[:8])
    check("magic KANG", magic == b"KANG")
    check("type preserved", mtype == J.WORK_ASN)
    check("version stamped 4 (client rejects anything else)", ver == 4, f"got {ver}")
    check("size matches payload", size == J.WORK_ASN_S.size)
    check("oversized frame rejected",
          _raises(lambda: J.pack_frame(1, b"x" * (J.MAX_MESSAGE_SIZE + 1))))
    check("struct sizes match client static_asserts",
          (J.HDR.size, J.AUTH_V2.size, J.WORK_ASN_S.size, J.DP_V2.size,
           J.DP_V3.size, J.STATS_RSP_S.size) == (8, 120, 126, 78, 114, 36),
          str((J.HDR.size, J.AUTH_V2.size, J.WORK_ASN_S.size, J.DP_V2.size,
               J.DP_V3.size, J.STATS_RSP_S.size)))


def test_helpers():
    print("\n[helpers]")
    check("dp_bits 8 accepts x < 2^248",
          J.has_leading_zero_bits(b"\x00" + b"\xff" * 31, 8))
    check("dp_bits 8 rejects x >= 2^248",
          not J.has_leading_zero_bits(b"\x01" + b"\x00" * 31, 8))
    check("negative d sign-extends",
          J.decode_d(((-5) % (1 << 256)).to_bytes(32, "big")) == -5)
    check("min 256-bit value decodes to -2^255",
          J.decode_d((1 << 255).to_bytes(32, "big")) == -(1 << 255))
    check("positive d decodes", J.decode_d((12345).to_bytes(32, "big")) == 12345)


def _raises(fn):
    try:
        fn()
        return False
    except Exception:
        return True


def test_key_math():
    print("\n[key reconstruction]")
    r0 = 1 << 19
    secret = r0 + 4242
    Q = J.pt_mul(secret)
    # collision: d_t*G == (Q - r0*G) + d_w*G  =>  d_t = (secret - r0) + d_w
    dw = 100
    dt = (secret - r0) + dw
    tame = {"d": dt.to_bytes(32, "big")}
    wild = {"d": dw.to_bytes(32, "big")}
    k = J.reconstruct_key(tame, wild, r0)
    check("reconstructed key equals secret", k == secret, f"got {k}")
    check("reconstructed key derives the target point", J.pt_mul(k) == Q)


def test_verify_dp():
    print("\n[dp verification]")
    r0 = 1 << 19
    secret = r0 + 999
    Q = J.pt_mul(secret)
    dt = 5000
    x = J.pt_mul(dt)[0].to_bytes(32, "big")
    check("valid tame DP accepted",
          J.verify_dp({"x": x, "d": dt.to_bytes(32, "big"), "type": J.DP_TYPE_TAME},
                      Q, r0, J.KANG_TAME))
    check("tampered tame DP rejected",
          not J.verify_dp({"x": x, "d": (dt + 1).to_bytes(32, "big"),
                           "type": J.DP_TYPE_TAME}, Q, r0, J.KANG_TAME))
    base = J.pt_add(Q, J.pt_mul(-r0))
    dw = 777
    wpt = J.pt_add(base, J.pt_mul(dw))
    check("valid wild DP accepted",
          J.verify_dp({"x": wpt[0].to_bytes(32, "big"),
                       "d": dw.to_bytes(32, "big"), "type": J.DP_TYPE_WILD},
                      Q, r0, J.KANG_WILD))


# ---------------------------------------------------------------------------
# End-to-end over a socket
# ---------------------------------------------------------------------------
def find_distinguished(secret, r0, dp_bits, limit=200000):
    """Find a wild distance dw such that (secret-r0+dw)*G has dp_bits zeros."""
    offset = secret - r0
    for dw in range(0, limit):
        pt = J.pt_mul(offset + dw)
        if pt and J.has_leading_zero_bits(pt[0].to_bytes(32, "big"), dp_bits):
            return dw
    return None


def test_end_to_end():
    print("\n[end-to-end over TCP]")
    dp_bits = 8
    r0 = 1 << 19
    secret = r0 + 31337
    Q = J.pt_mul(secret)
    pub33 = J.compress(Q)

    state = J.PoolState(pub33, r0, (1 << 20) - 1, dp_bits)
    srv = J.Server(("127.0.0.1", 0), state)
    port = srv.server_address[1]
    t = threading.Thread(target=srv.serve_forever, daemon=True)
    t.start()

    dw = find_distinguished(secret, r0, dp_bits)
    check("found a distinguished wild point", dw is not None)
    if dw is None:
        srv.shutdown()
        return
    dt = (secret - r0) + dw
    x = J.pt_mul(dt)[0].to_bytes(32, "big")

    sock = socket.create_connection(("127.0.0.1", port), timeout=10)

    worker = b"bc1qtestworker".ljust(64, b"\x00")
    auth = J.AUTH_V2.pack(worker, b"".ljust(32, b"\x00"),
                          int(time.time() * 1000), b"\x11" * 16)
    sock.sendall(J.pack_frame(J.AUTH, auth))
    mt, _, pl = J.read_frame(sock)
    check("AUTH accepted", mt == J.AUTH_OK, f"got {mt:#x}")

    # ---- tame assignment ------------------------------------------------
    sock.sendall(J.pack_frame(J.WORK_REQ, b""))
    mt, _, pl = J.read_frame(sock)
    check("WORK_ASN received", mt == J.WORK_ASN)
    pk, rs, re_, db, wid, ktype, a, b = J.WORK_ASN_S.unpack(pl)
    check("assignment echoes the target pubkey", pk == pub33)
    check("dp_bits honoured", db == dp_bits)
    check("first assignment is TAME", ktype == J.KANG_TAME, f"got {ktype}")

    tame_dp = J.DP_V2.pack(wid, 0, x, dt.to_bytes(32, "big"),
                           J.DP_TYPE_TAME, dp_bits)
    sock.sendall(J.pack_frame(J.DP_BATCH_V2, struct.pack("<I", 1) + tame_dp))
    mt, _, pl = J.read_frame(sock)
    n = struct.unpack("<I", pl)[0]
    check("tame DP acknowledged", mt == J.DP_ACK and n == 1, f"n={n}")

    # ---- wild assignment ------------------------------------------------
    sock.sendall(J.pack_frame(J.WORK_REQ, b""))
    mt, _, pl = J.read_frame(sock)
    pk, rs, re_, db, wid2, ktype2, a, b = J.WORK_ASN_S.unpack(pl)
    check("second assignment is WILD", ktype2 == J.KANG_WILD, f"got {ktype2}")

    wild_dp = J.DP_V2.pack(wid2, 0, x, dw.to_bytes(32, "big"),
                           J.DP_TYPE_WILD, dp_bits)
    sock.sendall(J.pack_frame(J.DP_BATCH_V2, struct.pack("<I", 1) + wild_dp))
    mt, _, pl = J.read_frame(sock)
    n = struct.unpack("<I", pl)[0]
    check("wild DP acknowledged", mt == J.DP_ACK and n == 1, f"n={n}")

    mt, _, pl = J.read_frame(sock)
    check("SOLUTION pushed", mt == J.SOLUTION, f"got {mt:#x}")
    if mt == J.SOLUTION:
        key = int.from_bytes(pl[:32], "big")
        check("SOLUTION carries the correct key", key == secret,
              f"got {key} want {secret}")
        check("key derives the target", J.pt_mul(key) == Q)

    # ---- stats ----------------------------------------------------------
    sock.sendall(J.pack_frame(J.STATS_REQ, b""))
    mt, _, pl = J.read_frame(sock)
    check("STATS_RSP received", mt == J.STATS_RSP and len(pl) == J.STATS_RSP_S.size)
    if mt == J.STATS_RSP:
        total, tw, aw, rate, share, mine, up = J.STATS_RSP_S.unpack(pl)
        check("stats counted both DPs", total == 2, f"total={total}")
        check("one worker registered", tw == 1, f"tw={tw}")
        check("your_dps counts this worker", mine == 2, f"mine={mine}")
        check("stats payload is 36 bytes", len(pl) == 36)

    # ---- rejection paths ------------------------------------------------
    sock.sendall(J.pack_frame(J.PING, b""))
    mt, _, _ = J.read_frame(sock)
    check("PING answered with PONG", mt == J.PONG)

    sock.close()
    srv.shutdown()
    srv.server_close()

    print("\n[negative: wrong-type DP is dropped]")
    s = J.PoolState(pub33, r0, (1 << 20) - 1, dp_bits)
    a_ = s.assign("w1")                 # tame
    wrong = J.DP_V2.pack(a_["work_id"], 0, x, dw.to_bytes(32, "big"),
                         J.DP_TYPE_WILD, dp_bits)
    n, _ = s.accept_batch("w1", struct.pack("<I", 1) + wrong)
    check("wild DP on a tame assignment rejected", n == 0, f"n={n}")

    print("\n[negative: stale work_id is dropped]")
    stale = J.DP_V2.pack(a_["work_id"] + 99, 0, x, dt.to_bytes(32, "big"),
                         J.DP_TYPE_TAME, dp_bits)
    n, _ = s.accept_batch("w1", struct.pack("<I", 1) + stale)
    check("mismatched work_id rejected", n == 0, f"n={n}")

    print("\n[v3 batch path]")
    s3b = J.PoolState(pub33, r0, (1 << 20) - 1, dp_bits)
    a2 = s3b.assign("w3")                    # tame
    rec3 = J.DP_V3.pack(a2["work_id"], 0, x, dt.to_bytes(32, "big"),
                        J.DP_TYPE_TAME, dp_bits, b"\x00" * 32, 4)
    n, err = s3b.accept_batch("w3", struct.pack("<I", 1) + rec3, v3=True)
    check("114-byte V3 record accepted", n == 1, f"n={n} err={err}")
    # a v2-sized buffer must not be silently read as v3
    n_bad, _ = s3b.accept_batch(
        "w3", struct.pack("<I", 1) + rec3[:J.DP_V2.size], v3=True)
    check("truncated V3 batch rejected", n_bad == 0, f"n={n_bad}")

    print("\n[negative: AUTH clock skew]")
    srv2 = J.Server(("127.0.0.1", 0), J.PoolState(pub33, r0, (1 << 20) - 1, dp_bits))
    p2 = srv2.server_address[1]
    threading.Thread(target=srv2.serve_forever, daemon=True).start()
    s2 = socket.create_connection(("127.0.0.1", p2), timeout=10)
    bad = J.AUTH_V2.pack(worker, b"".ljust(32, b"\x00"),
                         int(time.time() * 1000) - 120_000, b"\x22" * 16)
    s2.sendall(J.pack_frame(J.AUTH, bad))
    mt, _, _ = J.read_frame(s2)
    check("stale timestamp refused", mt == J.AUTH_FAIL, f"got {mt:#x}")
    s2.close()
    srv2.shutdown()
    srv2.server_close()

    print("\n[negative: old protocol version]")
    srv3 = J.Server(("127.0.0.1", 0), J.PoolState(pub33, r0, (1 << 20) - 1, dp_bits))
    p3 = srv3.server_address[1]
    threading.Thread(target=srv3.serve_forever, daemon=True).start()
    s3 = socket.create_connection(("127.0.0.1", p3), timeout=10)
    good = J.AUTH_V2.pack(worker, b"".ljust(32, b"\x00"), int(time.time() * 1000),
                          b"\x33" * 16)
    s3.sendall(J.pack_frame(J.AUTH, good, version=2))
    mt, _, _ = J.read_frame(s3)
    check("v2 client refused", mt == J.AUTH_FAIL, f"got {mt:#x}")
    s3.close()
    srv3.shutdown()
    srv3.server_close()




def test_strict_client_compat():
    """Replay the shipped client's own validation rules against our frames.

    Mirrors jlp_pool_client.cpp read_message():
      - magic must be KANG
      - header.flags must EQUAL PROTOCOL_VERSION (4)  <- disconnects otherwise
      - payload_size must be <= 65535
    Mirrors handle_work_asn(): payload size must be EXACTLY sizeof(WorkAssignment)
    Mirrors handle_stats_rsp(): payload must be >= 36
    Mirrors handle_auth_ok(): payload < sizeof(AuthOkPayload) == "no advert"
                              (a populated advert would trigger auto-update)
    """
    print("\n[strict client compatibility]")
    PROTOCOL_VERSION = 4          # jlp_wire_generated.hpp
    import socket as _s
    r0 = 1 << 19
    secret = r0 + 12345
    pub33 = J.compress(J.pt_mul(secret))
    srv = J.Server(("127.0.0.1", 0), J.PoolState(pub33, r0, (1 << 20) - 1, 8))
    port = srv.server_address[1]
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    sock = _s.create_connection(("127.0.0.1", port), timeout=10)

    def raw_frame():
        hdr = b""
        while len(hdr) < 8:
            hdr += sock.recv(8 - len(hdr))
        magic, mtype, flags, size = J.HDR.unpack(hdr)
        body = b""
        while len(body) < size:
            body += sock.recv(size - len(body))
        return magic, mtype, flags, size, body

    sock.sendall(J.pack_frame(J.AUTH, J.AUTH_V2.pack(
        b"w".ljust(64, b"\x00"), b"\x00" * 32,
        int(time.time() * 1000), b"\x44" * 16), version=PROTOCOL_VERSION))

    magic, mtype, flags, size, body = raw_frame()
    check("AUTH_OK magic is KANG", magic == b"KANG")
    check("AUTH_OK version byte == 4 (client equality check)",
          flags == PROTOCOL_VERSION, f"got {flags}")
    check("AUTH_OK carries no update advert",
          size < 388, f"size={size} would trigger auto-update")

    sock.sendall(J.pack_frame(J.WORK_REQ, b"", version=PROTOCOL_VERSION))
    magic, mtype, flags, size, body = raw_frame()
    check("WORK_ASN version byte == 4", flags == PROTOCOL_VERSION, f"got {flags}")
    check("WORK_ASN payload is EXACTLY 126 bytes", size == 126, f"got {size}")

    sock.sendall(J.pack_frame(J.STATS_REQ, b"", version=PROTOCOL_VERSION))
    magic, mtype, flags, size, body = raw_frame()
    check("STATS_RSP version byte == 4", flags == PROTOCOL_VERSION, f"got {flags}")
    check("STATS_RSP payload >= 36 bytes", size >= 36, f"got {size}")
    check("STATS_RSP unpacks as <QIIffQI>", len(J.STATS_RSP_S.unpack(body)) == 7)

    sock.sendall(J.pack_frame(J.PING, b"", version=PROTOCOL_VERSION))
    magic, mtype, flags, size, body = raw_frame()
    check("PONG version byte == 4", flags == PROTOCOL_VERSION, f"got {flags}")

    sock.close()
    srv.shutdown()
    srv.server_close()


def main():
    global FAILS
    t0 = time.time()
    print("JLP v3 server test suite")
    test_curve(); test_ripemd(); test_framing(); test_helpers(); test_key_math()
    test_verify_dp(); test_end_to_end(); test_strict_client_compat()
    print(f"\n{'=' * 46}")
    if FAILS:
        print(f"\033[31m{len(FAILS)} FAILED\033[0m: {', '.join(FAILS)}")
        return 1
    print(f"\033[32mall tests passed\033[0m in {time.time() - t0:.1f}s")
    return 0


if __name__ == "__main__":
    sys.exit(main())
