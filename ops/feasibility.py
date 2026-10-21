#!/usr/bin/env python3
"""
feasibility.py - how long would each remaining Bitcoin puzzle take, at MY rate?

The 1000 BTC puzzle: address N holds a key in [2^(N-1), 2^N). Only puzzles whose
public key is already exposed can be attacked with Pollard's Kangaroo; the rest
need brute force over the whole range.

Public keys are exposed only for puzzles that have had funds moved *from* them,
which historically means the multiples of 5 (and every puzzle <= 70, all solved).

Inputs are measured, not assumed:
    DP/s      from your own --verbose log:  DPs submitted / seconds elapsed
    keys/s    from `collider --benchmark`

Usage:
    python3 ops/feasibility.py --rate 1.5
    python3 ops/feasibility.py --rate 1.5 --dp-bits 28 --year 365.25
    python3 ops/feasibility.py --rate 30 --gpu-note "one RTX 4090"

Everything is an EXPECTATION. A real solve can land at 0.5x-2x these numbers,
and that spread is the point: the low end is still centuries.
"""

import argparse
import math

SECONDS_PER_YEAR = 365.25 * 24 * 3600  # Julian year, matching the pool's own ETA

# Remaining unsolved puzzles that ARE kangaroo-able (pubkey exposed).
# 1-70 solved; multiples of 5 from 75-130 solved; #135 solved June 2026.
KANGAROO_PUZZLES = [140, 145, 150, 155, 160]

# Pool's published expectation for #140 (from collisionprotocol.com/pool:
# "388.97M DPs collected ~1788.19B expected").
POOL_EXPECTED_DPS_140 = 1788.19e9

# Textbook kangaroo expectation: ~2*sqrt(range) group ops, x1.15 for the K=1.15
# variant. Range = 2^(N-1).
def textbook_ops(n):
    return 1.15 * 2 * math.sqrt(2 ** (n - 1))


def fmt_time(seconds):
    years = seconds / SECONDS_PER_YEAR
    if years < 1:
        return f"{seconds/86400:.1f} days"
    if years < 1000:
        return f"{years:,.0f} years"
    if years < 1e6:
        return f"{years/1000:,.0f} thousand years"
    if years < 1e9:
        return f"{years/1e6:,.1f} million years"
    return f"{years/1e9:,.1f} billion years"


def fmt_int(n):
    for div, suf in ((1e15, "Q"), (1e12, "T"), (1e9, "B"), (1e6, "M"), (1e3, "K")):
        if abs(n) >= div:
            return f"{n/div:,.2f}{suf}"
    return f"{n:,.0f}"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--rate", type=float, required=True,
                    help="your measured DP/s (from the verbose log)")
    ap.add_argument("--dp-bits", type=int, default=28,
                    help="pool dp_bits (player shows 28)")
    ap.add_argument("--pool-rate", type=float, default=2.6,
                    help="current pool-wide DP/s, for comparison")
    ap.add_argument("--gpu-note", default="", help="label for the --rate value")
    ap.add_argument("--prize-per-bit", type=float, default=0.1,
                    help="BTC per puzzle number (prize is N/10 BTC, e.g. #140 = 14 BTC)")
    ap.add_argument("--watts", type=float, default=60,
                    help="wall power draw while hunting, for the cost column")
    ap.add_argument("--kwh-price", type=float, default=0.15,
                    help="electricity price per kWh")
    ap.add_argument("--fee", type=float, default=0.05, help="pool fee fraction")
    args = ap.parse_args()

    rate = args.rate
    label = f" ({args.gpu_note})" if args.gpu_note else ""

    print("=" * 78)
    print(f"  AT YOUR RATE: {rate:,.2f} DP/s{label}")
    print("=" * 78)
    print()
    print(f"  A year of running earns {rate * SECONDS_PER_YEAR:,.0f} DPs")
    print(f"  Your implied key rate: ~{rate * 2**args.dp_bits / 1e9:,.2f} GKeys/s"
          f"  (DP/s x 2^{args.dp_bits})")

    # ---------------------------------------------------------------- target
    print()
    print("-" * 78)
    print("  1. HOW LONG TO SOLVE, USING THE POOL'S OWN EXPECTATION")
    print("-" * 78)
    print("  Pool publishes 1,788.19B DPs expected for #140 "
          "(0.0218% covered so far).")
    print("  Work scales as sqrt(range), i.e. x2^(delta/2) per +delta puzzle.")
    print()
    print(f"  {'puzzle':>7} {'net BTC':>9} {'exp. DPs':>10} "
          f"{'at your rate':>22} {'pool-wide':>16}")
    print(f"  {'':>7} {'(5% fee)':>9} {'':>10} {'':>22} {'(2.6 DP/s)':>16}")
    print("  " + "-" * 74)

    for n in KANGAROO_PUZZLES:
        dps = POOL_EXPECTED_DPS_140 * 2 ** ((n - 140) / 2)
        prize = n * args.prize_per_bit * (1 - args.fee)  # net of pool fee
        print(f"  {n:>7} {prize:>9.3f} {fmt_int(dps):>10} "
              f"{fmt_time(dps / rate):>22} {fmt_time(dps / args.pool_rate):>16}")

    print()
    print("  Everything else (71, 72, 73, 74, 76-79, 81-84, ...) has NO exposed")
    print("  public key, so kangaroo does not apply. Brute force over 2^(N-1):")
    n = 71
    ops = 2 ** (n - 1)
    for guess, name in ((1e9, "1 GKey/s (fast desktop GPU)"),
                        (5e8, "500 MKey/s (typical laptop GPU)")):
        yrs = ops / guess / SECONDS_PER_YEAR
        print(f"    puzzle #{n} at {name:<34} = {yrs:,.0f} years")
    print("  The smallest brute-force-only puzzle is already out of reach.")

    # ------------------------------------------------------------- variance
    print()
    print("-" * 78)
    print("  2. THE HONEST ERROR BAR")
    print("-" * 78)
    t140 = POOL_EXPECTED_DPS_140 / rate
    print(f"  Textbook kangaroo (~2*sqrt(range)) gives "
          f"{fmt_int(textbook_ops(140) / 2**args.dp_bits)} DPs for #140,")
    print(f"  about 2.8x the pool's own figure. A solve can also land at 0.5x-2x")
    print(f"  the expectation. So #140 for you is somewhere in:")
    print(f"        optimistic   {fmt_time(t140 * 0.5):>20}")
    print(f"        pool model   {fmt_time(t140):>20}")
    print(f"        textbook     {fmt_time(textbook_ops(140) / 2**args.dp_bits / rate):>20}")
    print()
    print("  All three answers round to 'never, for one machine'.")

    # ------------------------------------------------------------ what it takes
    print()
    print("-" * 78)
    print("  3. WHAT #140 WOULD ACTUALLY REQUIRE")
    print("-" * 78)
    print(f"  {'target horizon':<20} {'DP/s needed':>14} {'vs you':>12} {'vs pool':>12}")
    print("  " + "-" * 62)
    for years, name in ((1, "1 year"), (5, "5 years"), (10, "10 years"),
                        (50, "50 years")):
        need = POOL_EXPECTED_DPS_140 / (years * SECONDS_PER_YEAR)
        print(f"  {name:<20} {need:>14,.0f} {need/rate:>11,.0f}x "
              f"{need/args.pool_rate:>11,.0f}x")

    gpus = POOL_EXPECTED_DPS_140 / (1 * SECONDS_PER_YEAR) / 30  # 4090 ~ 30 DP/s
    print()
    print(f"  #140 inside one year needs ~{gpus:,.0f} RTX-4090-class cards")
    print(f"  (at 30 DP/s each). Your {rate} DP/s is roughly "
          f"{rate/30:.2f} of one 4090.")

    # ------------------------------------------------------------- your share
    print()
    print("-" * 78)
    print("  4. WHAT YOUR UPTIME IS WORTH")
    print("-" * 78)
    print("  Share at solve = your lifetime DPs / DPs the pool needs in total.")
    print("  (That total is the same 1.789T; the pool stops at the collision.)")
    print()
    print(f"  {'you run for':<14} {'your DPs':>12} {'share at solve':>16} {'net payout':>14}")
    print("  " + "-" * 58)
    base = 1.40e6  # already accrued
    for years in (1, 3, 5, 10):
        mine = base + rate * SECONDS_PER_YEAR * years
        share = mine / POOL_EXPECTED_DPS_140
        net = share * (140 * args.prize_per_bit) * (1 - args.fee)
        print(f"  {str(years)+' year'+(('s') if years>1 else ''):<14} "
              f"{fmt_int(mine):>12} {share*100:>15.5f}% {net:>14.6f}")
    print()
    print(f"  Electricity over that time ({args.watts:.0f} W, "
          f"${args.kwh_price:.2f}/kWh):")
    for years in (1, 5):
        kwh = args.watts * 24 * 365.25 * years / 1000
        print(f"  {str(years)+' year'+('s' if years>1 else ''):<14} "
              f"{kwh:>10,.0f} kWh  = ${kwh * args.kwh_price:,.0f}")
    print()
    print("  Caveat: this assumes the pool eventually reaches 1.789T DPs. It has")
    print(f"  {388.97e6/POOL_EXPECTED_DPS_140*100:.4f}% of that, with 3 active workers and a")
    print("  published ETA of ~21,789 years. Scaling the payout by that coverage")
    print("  is the only honest adjustment left:")
    p_solve = 388.97e6 / POOL_EXPECTED_DPS_140
    mine = base + rate * SECONDS_PER_YEAR * 10
    print(f"        {(mine/POOL_EXPECTED_DPS_140)*100:.5f}% x 14 BTC x {p_solve*100:.4f}% "
          f"= {mine/POOL_EXPECTED_DPS_140*14*p_solve*(1-args.fee):.2e} BTC")
    print("  for ten years of a laptop running hot.")

    print("=" * 78)


if __name__ == "__main__":
    main()
