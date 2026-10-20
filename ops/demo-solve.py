#!/usr/bin/env python3
"""
demo-solve.py - end-to-end smoke test of the local pool as an actual process.

Generates a small puzzle, launches `jlp_server.py --puzzle ...` as a subprocess,
plays the part of the miner over TCP (AUTH, take a TAME job, send a matching
tame DP, take a WILD job, send the colliding wild DP), then asserts that the
server pushed SOLUTION, that solved.txt was written, and that check-solution.py
accepts the key.

    python3 ops/demo-solve.py

This does not use a GPU. It is the protocol path the real collider exercises,
just with the search done in Python on a 24-bit range so it finishes instantly.
"""

import json
import os
import socket
import struct
import subprocess
import sys
import tempfile
import time

import jlp_server as J

HERE = os.path.dirname(os.path.abspath(__file__))
BITS = 24


def find_wild(secret, r0, dp_bits, limit=1 << 20):
    """Smallest dw whose (secret-r0+dw)*G is distinguished."""
    offset = secret - r0
    for dw in range(limit):
        pt = J.pt_mul(offset + dw)
        if pt and J.has_leading_zero_bits(pt[0].to_bytes(32, "big"), dp_bits):
            return dw
    return None


def free_port():
    s = socket.socket()
    s.bind(("127.0.0.1", 0))
    p = s.getsockname()[1]
    s.close()
    return p


def main():
    tmp = tempfile.mkdtemp(prefix="jlp-demo-")
    pz = os.path.join(tmp, "puzzle.json")
    subprocess.run([sys.executable, os.path.join(HERE, "make-puzzle.py"),
                    "--bits", str(BITS), "--dp-bits", "8", "--out", pz],
                   check=True, capture_output=True)
    with open(pz) as fh:
        p = json.load(fh)

    secret = int(p["secret_hex"], 16)
    r0 = int(p["range_start"], 16)
    dp_bits = p["dp_bits"]
    port = free_port()
    solved = os.path.join(tmp, "solved.txt")

    srv = subprocess.Popen(
        [sys.executable, os.path.join(HERE, "jlp_server.py"),
         "--pubkey", p["pubkey"], "--range-start", p["range_start"],
         "--range-end", p["range_end"], "--dp-bits", str(dp_bits),
         "--port", str(port), "--puzzle", pz, "--solved", solved],
        stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)

    try:
        # wait for the listener
        for _ in range(100):
            try:
                socket.create_connection(("127.0.0.1", port), timeout=0.5).close()
                break
            except OSError:
                time.sleep(0.1)
        else:
            sys.exit("server never listened")

        sock = socket.create_connection(("127.0.0.1", port), timeout=15)
        auth = J.AUTH_V2.pack(b"demo-miner".ljust(64, b"\x00"),
                              b"\x00" * 32, int(time.time() * 1000), b"\x01" * 16)
        sock.sendall(J.pack_frame(J.AUTH, auth))
        mt, _, _ = J.read_frame(sock)
        assert mt == J.AUTH_OK, f"AUTH failed: {mt:#x}"

        # --- TAME job -----------------------------------------------------
        sock.sendall(J.pack_frame(J.WORK_REQ, b""))
        _, _, pl = J.read_frame(sock)
        _, _, _, _, wid, ktype, _, _ = J.WORK_ASN_S.unpack(pl)
        assert ktype == J.KANG_TAME, "expected TAME"

        dw = find_wild(secret, r0, dp_bits)
        assert dw is not None, "no distinguished point found"
        dt = (secret - r0) + dw
        x = J.pt_mul(dt)[0].to_bytes(32, "big")
        print(f"  wild d = {dw}, tame d = {dt}")

        sock.sendall(J.pack_frame(J.DP_BATCH_V2, struct.pack("<I", 1) + J.DP_V2.pack(
            wid, 0, x, dt.to_bytes(32, "big"), J.DP_TYPE_TAME, dp_bits)))
        mt, _, pl = J.read_frame(sock)
        assert mt == J.DP_ACK and struct.unpack("<I", pl)[0] == 1, "tame DP rejected"

        # --- WILD job -----------------------------------------------------
        sock.sendall(J.pack_frame(J.WORK_REQ, b""))
        _, _, pl = J.read_frame(sock)
        _, _, _, _, wid2, ktype2, _, _ = J.WORK_ASN_S.unpack(pl)
        assert ktype2 == J.KANG_WILD, "expected WILD"
        sock.sendall(J.pack_frame(J.DP_BATCH_V2, struct.pack("<I", 1) + J.DP_V2.pack(
            wid2, 0, x, dw.to_bytes(32, "big"), J.DP_TYPE_WILD, dp_bits)))
        mt, _, pl = J.read_frame(sock)
        assert mt == J.DP_ACK, "wild DP rejected"

        # --- SOLUTION -----------------------------------------------------
        mt, _, pl = J.read_frame(sock)
        assert mt == J.SOLUTION, f"expected SOLUTION, got {mt:#x}"
        key = int.from_bytes(pl[:32], "big")
        assert key == secret, "server recovered the WRONG key"
        print(f"  server pushed key {key:064x}")
        sock.close()

        # --- side effects and independent verification ---------------------
        time.sleep(0.4)
        assert os.path.exists(solved), "solved.txt was not written"
        with open(solved) as fh:
            written = fh.read().strip()
        assert int(written, 16) == secret, "solved.txt has the wrong key"
        print(f"  solved.txt written: {written}")

        r = subprocess.run([sys.executable, os.path.join(HERE, "check-solution.py"),
                            "--puzzle", pz, "--key", written],
                           capture_output=True, text=True)
        assert r.returncode == 0, "check-solution.py rejected the key"
        print("  check-solution.py: VERIFIED")

    finally:
        srv.terminate()
        try:
            out = srv.communicate(timeout=5)[0]
        except subprocess.TimeoutExpired:
            srv.kill()
            out = srv.communicate()[0]

    print("\n  \033[32mEND-TO-END PASS\033[0m - server process solved the puzzle "
          "and the key was independently verified.")
    if "-v" in sys.argv:
        print("\n--- server log ---\n" + out)
    return 0


if __name__ == "__main__":
    sys.exit(main())
