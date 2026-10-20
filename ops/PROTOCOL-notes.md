# JLP wire protocol — verified notes

Everything here was checked against two sources, not guessed:

1. `hevnsnt/collider` → `docs/JLP-PROTOCOL.md` (562 lines, 34 sections)
2. `hevnsnt/collider` → `src/pool/jlp_wire_generated.hpp` and
   `src/pool/jlp_pool_client.cpp` (the shipped client's actual parser)

Source 2 wins wherever the two disagree, because that is the code that will
actually connect to us.

---

## 1. Frame header — 8 bytes, little-endian

| Offset | Size | Field | Notes |
| ---: | ---: | --- | --- |
| 0 | 4 | `magic` | `KANG` |
| 4 | 1 | `type` | message code |
| 5 | 1 | `flags` | protocol version |
| 6 | 2 | `payload_size` | uint16 LE, max 65535 |

`MAX_MESSAGE_SIZE = 1048576` (1 MiB) in the IDL, but the wire field is 16-bit,
so 65535 is the real per-frame cap.

## 2. ⚠ The version byte is an exact match, not a negotiation

The spec says the server computes `negotiated_version = min(client_flags, 4)`
and stamps *that* into outbound frames. **The shipped client does not accept
that.** In `jlp_pool_client.cpp`, `read_message()`:

```cpp
if (header.flags != jlp_wire::PROTOCOL_VERSION) {
    std::cerr << "[Pool] Protocol version mismatch: server sent flags=..."
              << " ... Disconnecting.\n";
    return false;
}
```

with `constexpr int PROTOCOL_VERSION = 4;` in `jlp_wire_generated.hpp`.

So: **every server→client frame must carry `flags == 4`.** A server that
faithfully implements `min()` and stamps 3 gets disconnected on the first
frame. This single line is the difference between a pool that works and one
that hangs on connect.

It cuts the other way too. The client also sends `flags = 4` even when it is
using the *unsigned* 120-byte AUTH body — comments in `send_hello()` note that
downgrading the flags to 3 "would get this client refused by a strict-mode
pool (floor 4)". So the flags byte tells you nothing about which AUTH body
shape arrived; **use the payload length** to decide.

## 3. Message codes

| Code | Name | Direction |
| ---: | --- | --- |
| 0x01 | `AUTH` | C→S |
| 0x02 | `AUTH_OK` | S→C |
| 0x03 | `AUTH_FAIL` | S→C |
| 0x10 | `WORK_REQ` | C→S |
| 0x11 | `WORK_ASN` | S→C |
| 0x20 | `DP_SUBMIT` | C→S (v1, deprecated) |
| 0x21 | `DP_ACK` | S→C |
| 0x22 | `DP_BATCH` | C→S (v1, deprecated) |
| 0x23 | `DP_SUBMIT_V2` | C→S (single, rare) |
| 0x24 | `DP_BATCH_V2` | C→S |
| 0x25 | `DP_SUBMIT_V3` | C→S (v4) |
| 0x26 | `DP_BATCH_V3` | C→S (v4) |
| 0x30 | `STATS_REQ` | C→S |
| 0x31 | `STATS_RSP` | S→C |
| 0x32 | `CHALLENGE` | S→C (v4) |
| 0x33 | `CHALLENGE_RSP` | C→S (v4) |
| 0x40 | `SOLUTION` | S→C |
| 0x50 | `PING` | C→S |
| 0x51 | `PONG` | S→C |
| 0x60 | `MAINTENANCE` | S→C (v4) |
| 0xFF | `MSG_ERROR` | either |

`MSG_ERROR` is not called `ERROR` because that collides with `<winerror.h>`.

## 4. Structs — all `#pragma pack(push, 1)`, verified by `static_assert`

| Struct | Size | Format | Fields |
| --- | ---: | --- | --- |
| `AuthPayload` | 96 | `<64s32s` | name, password (v1 legacy) |
| `AuthPayloadV2` | 120 | `<64s32sQ16s` | `worker_name[64]`, `password[32]`, `timestamp_ms` u64, `nonce[16]` |
| `AuthOkPayload` | 388 | see below | update advert, v4 only |
| `MaintenancePayload` | 262 | `<BBI256s` | reason, retry_after_s, message |
| `WorkAssignment` | 126 | `<33s32s32sIQBQQ` | `public_key[33]`, `range_start[32]`, `range_end[32]`, `dp_bits` u32, `work_id` u64, `kangaroo_type` u8, `start_offset_a` u64, `start_offset_b` u64 |
| `DistinguishedPoint` | 66 | `<Q32s32sB` | v1 |
| `DistinguishedPointV2` | 78 | `<QI32s32sBB` | `work_id` u64, `sequence` u32, `x[32]`, `d[32]`, `type` u8, `dp_bits` u8 |
| `DistinguishedPointV3` | 114 | `<QI32s32sBB32sI` | v2 fields + `ckpt_root[32]`, `n_segments` u32 |
| `PoolStats` | 36 | `<QIIffQI` | see §6 |

**`worker_name` is the BTC payout address, used verbatim as the credit key.**
The pool never validates that it is a well-formed address — a typo silently
credits an address nobody controls. Validate before connecting.

`AuthOkPayload` (388 B): `<16s16sB3s256s32s` = latest_version, min_version,
flags (bit0 update_available, bit1 maintenance_active), reserved[3],
download_url[256], sha256[32], **plus a trailing 64-byte `manifest_sig`**.

`kangaroo_type`: `1` = TAME, `2` = WILD, `0` is illegal in pool mode.
DP `type`: `0` = tame, `1` = wild. Note these two enums do **not** share
numbering — a conversion bug here silently drops every DP.

`dp_bits` valid range 8..32.

## 5. Sign conventions

`d` is a **signed** 32-byte big-endian integer, two's complement. Decode as
`v = int.from_bytes(d,'big'); if v >= 2**255: v -= 2**256`. Not mod N — mod
2^256.

The distinguishing rule is on `x` only: a DP is distinguished iff
`x < 2**(256 - dp_bits)`, i.e. `dp_bits` leading zero bits.

Verification:

```
tame:  (d * G).x            == x
wild:  ((Q - range_start*G) + d*G).x == x
```

Recovery, from `d_tame*G == (Q - range_start*G) + d_wild*G`:

```
secret = range_start + d_tame - d_wild   (mod N)
```

## 6. `PoolStats` / `STATS_RSP` — 36 bytes, `<QIIffQI`

| Offset | Size | Field |
| ---: | ---: | --- |
| 0 | 8 | `total_dps` u64 |
| 8 | 4 | `total_workers` u32 |
| 12 | 4 | `active_workers` u32 (last 5 min) |
| 16 | 4 | `dps_per_second` f32 |
| 20 | 4 | `your_share` f32 (0..1) |
| 24 | 8 | `your_dps` u64 (aggregate across all machines on this worker name) |
| 32 | 4 | `uptime_seconds` u32 |

The client ignores payloads shorter than 36 B and sanitizes NaN/Inf floats.

## 7. Behaviours that actually matter

- **`WORK_ASN` must be *exactly* 126 bytes.** The client rejects any other
  size ("audit pool HIGH-2") — it does not tolerate a longer payload.
- **`AUTH_OK` may be zero-payload.** The client treats anything shorter than
  `sizeof(AuthOkPayload)` as "no advert" and clears its cache. Sending zero
  payload is therefore the safe choice — and it is also the *only* safe
  choice, because a populated advert makes the client attempt an in-band
  **auto-update**: fetch a binary over HTTPS, verify sha256, replace itself,
  relaunch. A self-hosted pool must never advertise an update. Leave
  `download_url` all-zero, or send nothing.
- **`DP_ACK` payload is ignored** by the client. Echoing the accepted count
  as u32 is conventional.
- **`SOLUTION` needs ≥32 bytes**; the client takes `payload[0:32]` as opaque
  "stop working" metadata and **does not store or print the key**. So the
  server must print the recovered key itself — the worker deliberately has no
  code path that handles a recovered private key (deleted in v1.5).
- **AUTH gates everything.** `WORK_ASN`, `SOLUTION`, `DP_ACK`, `STATS_RSP`
  arriving before `AUTH_OK` are logged and ignored.
- **AUTH timing:** must arrive within `AUTH_TIMEOUT_SECS = 30` of connect.
  `timestamp_ms` must be within ±30 s or `auth_clock_skew (0x12)`. A reused
  `nonce` is `auth_nonce_reuse (0x13)`.
- **Keepalive:** client pings every `KEEPALIVE_SECS = 20`s.
- **Seq window:** accept a DP iff `sequence > high_water - 1024`; otherwise
  `dp_sequence_out_of_window (0x21)`. A wrong-type or stale DP is recoverable
  (re-request work), never a ban.
- **Challenges are off by default** (`challenge_mode: off`, also `shadow`,
  `enforce`). A v3-shaped client is never challenged.

## 8. TLS

`jlps://` = TLS 1.2/1.3, system trust store, SNI, `NO_PARTIAL_WILDCARDS`.
A failed trust-store load aborts at init rather than falling back to
no-verify. `jlp://` is plaintext.

For a loopback self-hosted pool, plaintext `jlp://` is correct and avoids
certificate work entirely. **Do not** expose `jlp://` across an untrusted
network — worker names and DPs travel in the clear.

## 9. Where the spec is simply missing

`SOLUTION`'s payload layout is "documented elsewhere" in the doc, and that
somewhere is gone. The client reads `payload[0:32]` and ignores the rest, so
32 bytes is sufficient and correct for v1.5.x. Older clients may have expected
a trailing `work_id`; if you ever need one, append `<Q` — the shipped client
will not care.
