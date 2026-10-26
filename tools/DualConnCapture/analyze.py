#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""双连接 × 物理状态 频率矩阵分析（严谨版）。

相对初版的改进：
1. 按「连续状态段」切分数据，段内才计算相邻帧间隔——避免跨状态切换产生的
   巨大间隔把中位数拉偏。
2. 每段剔除开头 --warmup-seconds 秒的过渡帧——状态刚切换时耳机广播行为尚未稳定。
3. 盒盖闭合单独成态（BaseClosed=1）——合盖后耳机组进入低发射状态，
   与开盖的"在盒"不是同一物理状态，混在一起会污染统计。
4. 段级聚合：先算每个段的中位间隔，再对「段中位数」取中位数。
   理由：广播呈"密集一阵 + 静默"的突发模式，把所有间隔混在一起会让帧多的段主导；
   段级聚合对段时长与帧数不敏感。
5. 输出每段的段频率列表，便于人工核对样本是否充分。

用法:
  python analyze.py [--csv 路径[,路径...]] [--on 手机开时刻 HH:MM:SS] [--off 手机关时刻 HH:MM:SS]
                    [--warmup-seconds 3] [--gap-seconds 8]
"""
import csv
import argparse
from statistics import median

def t2s(s: str) -> float:
    h, m, rest = s.split(":")
    return int(h) * 3600 + int(m) * 60 + float(rest)

def phys_state(r) -> str:
    # 盒盖闭合时耳机组进入休眠/低发射状态，广播语义与开盖不同，必须单独成态，
    # 否则合盖段会被错归入"双耳在盒"（实测会造出 4 倍以上的虚假频率差）。
    if r.get("BaseClosed") == 1:
        return "盒盖闭合"
    l = r["LeftInCase"] == 1
    rr = r["RightInCase"] == 1
    if l and rr:
        return "双耳在盒"
    if not l and not rr:
        return "双耳离盒"
    return "仅左在盒" if l else "仅右在盒"

def phys_group(r) -> str:
    s = phys_state(r)
    return "单耳在盒" if s in ("仅左在盒", "仅右在盒") else s

# 参与"单连接 vs 双连接"对比的物理状态档位（盒盖闭合仅作参考，不参与对比）
MATRIX_GROUPS = ["双耳在盒", "单耳在盒", "双耳离盒"]

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--csv", default=r"d:\code\wireless-battery\testdata\dualconn-capture.csv",
                    help="CSV 路径，多个用逗号分隔")
    ap.add_argument("--on", default="13:20:00", help="手机蓝牙开启(进入双连接)时刻")
    ap.add_argument("--off", default="13:23:00", help="手机蓝牙关闭(回到单连接)时刻")
    ap.add_argument("--warmup-seconds", type=float, default=3.0, help="每段剔除的过渡秒数")
    ap.add_argument("--gap-seconds", type=float, default=8.0, help="超过此间隔视为新段")
    args = ap.parse_args()

    ton, toff = t2s(args.on), t2s(args.off)
    rows = []
    for path in args.csv.split(","):
        path = path.strip()
        if not path:
            continue
        with open(path, encoding="utf-8-sig", newline="") as f:
            for r in csv.DictReader(f):
                r["_t"] = t2s(r["LocalTime"])
                r["_src"] = path
                for k in ("Left", "Right", "Case", "LeftInCase", "RightInCase", "BaseClosed"):
                    try:
                        r[k] = int(r[k], 0) if isinstance(r[k], str) and r[k].startswith("0x") else int(r[k])
                    except (ValueError, TypeError):
                        pass
                rows.append(r)

    rows.sort(key=lambda r: (r["_t"], r["_src"]))

    def conn_of(r):
        return "双连接" if ton <= r["_t"] < toff else "单连接"

    # 切分连续状态段：物理状态相同、相邻间隔小于 gap，且来自同一文件
    segments = []
    cur = []
    for r in rows:
        if cur:
            prev = cur[-1]
            same_state = phys_state(prev) == phys_state(r)
            same_file = prev["_src"] == r["_src"]
            gap = r["_t"] - prev["_t"]
            if not (same_state and same_file and gap <= args.gap_seconds):
                segments.append(cur)
                cur = []
        cur.append(r)
    if cur:
        segments.append(cur)

    # 每段剔除开头过渡帧，段内计算相邻间隔 -> 段中位间隔
    print("===== 状态段清单 =====")
    seg_freq = {}      # (state, conn) -> [段频率 条/分, 段中位间隔 秒]
    seg_meta = {}      # (state, conn) -> [(起, 止, 段长, 有效帧数, 电量)]
    seg_stats = {}     # (state, conn) -> [帧数, 时长]
    for seg in segments:
        if len(seg) < 3:
            continue
        state = phys_group(seg[0])
        conn = conn_of(seg[0])
        t0 = seg[0]["_t"]
        warm = t0 + args.warmup_seconds
        kept = [r for r in seg if r["_t"] >= warm]
        if len(kept) < 3:
            continue

        intervals = []
        for i in range(1, len(kept)):
            dt = kept[i]["_t"] - kept[i - 1]["_t"]
            if dt > 0:
                intervals.append(dt)
        if not intervals:
            continue

        med = median(intervals)
        freq = 1 / med * 60
        key = (state, conn)
        seg_freq.setdefault(key, []).append((freq, med))
        span = kept[-1]["_t"] - kept[0]["_t"]
        seg_stats.setdefault(key, []).append((len(kept), span))
        L = sorted(set(r["Left"] for r in kept))
        R = sorted(set(r["Right"] for r in kept))
        C = sorted(set(r["Case"] for r in kept))
        seg_meta.setdefault(key, []).append((seg[0]["LocalTime"], seg[-1]["LocalTime"], seg[-1]["_t"] - t0, len(kept), (L, R, C)))

        print(f"  {state:<10} {conn:<6} {seg[0]['LocalTime']} ~ {seg[-1]['LocalTime']}  "
              f"持续 {seg[-1]['_t']-t0:5.1f}s  有效帧 {len(kept):4d}  "
              f"段中位间隔 {med*1000:6.1f}ms  段频率 {freq:6.1f}条/分  "
              f"吞吐 {len(kept)/span*60 if span else 0:6.1f}条/分  电量 {L}/{R}/{C}")

    def throughput(key):
        """口径A：吞吐率 = 有效帧数总和 / 有效时长总和（条/分）。反映实际产出。"""
        st = seg_stats.get(key)
        if not st:
            return None
        frames = sum(f for f, _ in st)
        span = sum(s for _, s in st)
        return frames / span * 60 if span else None

    def typical_freq(key):
        """口径B：典型发射频率 = 1 / (各段中位间隔的中位数)。反映发射节奏。"""
        vals = seg_freq.get(key)
        if not vals:
            return None
        return 1 / median([m for _, m in vals]) * 60

    conns = ["单连接", "双连接"]

    print("\n===== 频率矩阵 口径A：吞吐率 (有效帧数/有效时长, 条/分) =====")
    print("物理状态 \\ 连接".ljust(12) + "".join(c.ljust(18) for c in conns))
    for g in MATRIX_GROUPS:
        line = g.ljust(12)
        for c in conns:
            v = throughput((g, c))
            line += (f"{v:10.1f}条/分" if v is not None else "—").ljust(18)
        print(line)

    print("\n===== 频率矩阵 口径B：典型发射频率 (段中位间隔的中位数, 条/分) =====")
    print("物理状态 \\ 连接".ljust(12) + "".join(c.ljust(18) for c in conns))
    for g in MATRIX_GROUPS:
        line = g.ljust(12)
        for c in conns:
            v = typical_freq((g, c))
            line += (f"{v:10.1f}条/分" if v is not None else "—").ljust(18)
        print(line)

    print("\n===== 各组合明细 =====")
    for g in MATRIX_GROUPS:
        for c in conns:
            key = (g, c)
            if key not in seg_freq:
                print(f"  {g} × {c}: 无样本")
                continue
            freqs = sorted(f for f, _ in seg_freq[key])
            tp = throughput(key)
            print(f"  {g} × {c}: 段数={len(freqs)}  段频率={['%.1f' % f for f in freqs]}  "
                  f"吞吐={tp:.1f}  典型={typical_freq(key):.1f} 条/分")
            for (st, en, dur, n, bat) in seg_meta[key]:
                print(f"        {st}~{en}  {dur:5.1f}s  帧{n:4d}  电量 左={bat[0]} 右={bat[1]} 盒={bat[2]}")

    print("\n===== 同一物理状态下 单连接 vs 双连接 对比（两种口径）=====")
    for g in MATRIX_GROUPS:
        s_tp = throughput((g, "单连接"))
        d_tp = throughput((g, "双连接"))
        s_ty = typical_freq((g, "单连接"))
        d_ty = typical_freq((g, "双连接"))
        if None in (s_tp, d_tp, s_ty, d_ty):
            print(f"  {g}: 数据不全")
            continue
        r_tp = d_tp / s_tp
        r_ty = d_ty / s_ty
        verdict = "双连接更高" if r_tp > 1.15 else ("双连接更低" if r_tp < 0.85 else "基本持平(差异在噪声内)")
        print(f"  {g:<10} 吞吐率 单 {s_tp:6.1f} → 双 {d_tp:6.1f} ({r_tp:4.2f}x)   "
              f"典型频率 单 {s_ty:6.1f} → 双 {d_ty:6.1f} ({r_ty:4.2f}x)   {verdict}")

if __name__ == "__main__":
    main()
