#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""双连接 × 物理状态 频率矩阵分析。

两种用法：
1. 矩阵模式（默认）
   python analyze.py --csv 数据.csv --on 手机开时刻 --off 手机关时刻
   输出「物理状态 × 连接数」的频率矩阵。

2. 建立期窗口模式（--events）
   python analyze.py --csv 数据.csv --events 开蓝牙时刻1,开蓝牙时刻2 --offs 关蓝牙时刻1,关蓝牙时刻2
   输出每次「打开手机蓝牙」之后按相对时间分窗（0-10s / 10-30s / 30-60s）的频率，
   用于判定双连接建立初期是否存在广播压制期。

分析口径（两种并列，避免口径歧义）：
- 吞吐率：有效帧数 / 有效时长（条/分），反映实际产出
- 典型发射频率：1 / (各段中位间隔的中位数)（条/分），反映发射节奏
广播呈「密集一阵 + 长静默」突发模式，两种口径在同一份数据上可能给出相反结论。

其它修正：
- BaseClosed=1 独立成「盒盖闭合」态（合盖段否则会被错归入「双耳在盒」）
- 段级聚合：先算每段中位间隔再聚合，每段等权，不受帧数影响
- --warmup-seconds 剔除每段开头过渡帧，--gap-seconds 识别中断段
"""
import csv
import argparse
from statistics import median

def t2s(s: str) -> float:
    h, m, rest = s.split(":")
    return int(h) * 3600 + int(m) * 60 + float(rest)

def phys_state(r) -> str:
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

MATRIX_GROUPS = ["双耳在盒", "单耳在盒", "双耳离盒"]

def load_rows(paths):
    rows = []
    for path in paths:
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
    return rows

def build_segments(rows, gap_seconds):
    segments = []
    cur = []
    for r in rows:
        if cur:
            prev = cur[-1]
            same_state = phys_state(prev) == phys_state(r)
            same_file = prev["_src"] == r["_src"]
            gap = r["_t"] - prev["_t"]
            if not (same_state and same_file and gap <= gap_seconds):
                segments.append(cur)
                cur = []
        cur.append(r)
    if cur:
        segments.append(cur)
    return segments

def seg_stats(rows, gap_seconds, warmup_seconds):
    """返回 (state, conn) -> (段频率列表, 段中位间隔列表, [(帧数,时长)]) 以及明细。"""
    out = {}
    meta = {}
    for seg in build_segments(rows, gap_seconds):
        if len(seg) < 3:
            continue
        state = phys_group(seg[0])
        t0 = seg[0]["_t"]
        kept = [r for r in seg if r["_t"] >= t0 + warmup_seconds]
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
        span = kept[-1]["_t"] - kept[0]["_t"]
        key = state
        out.setdefault(key, ([], [], []))
        out[key][0].append(1 / med * 60)
        out[key][1].append(med)
        out[key][2].append((len(kept), span))
        meta.setdefault(key, []).append((seg[0]["LocalTime"], seg[-1]["LocalTime"], span, len(kept)))
    return out, meta

def throughput(frames_span):
    if not frames_span:
        return None
    frames = sum(f for f, _ in frames_span)
    span = sum(s for _, s in frames_span)
    return frames / span * 60 if span else None

def typical(medians):
    if not medians:
        return None
    return 1 / median(medians) * 60

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--csv", default=r"d:\code\wireless-battery\testdata\dualconn-capture.csv")
    ap.add_argument("--on", default="13:20:00")
    ap.add_argument("--off", default="13:23:00")
    ap.add_argument("--warmup-seconds", type=float, default=3.0)
    ap.add_argument("--gap-seconds", type=float, default=8.0)
    ap.add_argument("--events", default="", help="每次打开手机蓝牙的时刻，逗号分隔")
    ap.add_argument("--offs", default="", help="每次关闭手机蓝牙的时刻，逗号分隔（与 events 一一对应）")
    ap.add_argument("--windows", default="0-10,10-30,30-60", help="相对事件时刻的窗口（秒），逗号分隔")
    args = ap.parse_args()

    paths = args.csv.split(",")
    rows = load_rows(paths)

    if args.events.strip():
        run_window_mode(rows, args)
        return

    run_matrix_mode(rows, args)

def run_matrix_mode(rows, args):
    ton, toff = t2s(args.on), t2s(args.off)

    def conn_of(r):
        return "双连接" if ton <= r["_t"] < toff else "单连接"

    print("===== 状态段清单 =====")
    seg_freq = {}
    seg_meta = {}
    seg_stats_map = {}
    for seg in build_segments(rows, args.gap_seconds):
        if len(seg) < 3:
            continue
        state = phys_group(seg[0])
        conn = conn_of(seg[0])
        t0 = seg[0]["_t"]
        kept = [r for r in seg if r["_t"] >= t0 + args.warmup_seconds]
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
        seg_stats_map.setdefault(key, []).append((len(kept), span))
        L = sorted(set(r["Left"] for r in kept))
        R = sorted(set(r["Right"] for r in kept))
        C = sorted(set(r["Case"] for r in kept))
        seg_meta.setdefault(key, []).append((seg[0]["LocalTime"], seg[-1]["LocalTime"], span, len(kept), (L, R, C)))
        print(f"  {state:<10} {conn:<6} {seg[0]['LocalTime']} ~ {seg[-1]['LocalTime']}  "
              f"持续 {span:5.1f}s  有效帧 {len(kept):4d}  段中位间隔 {med*1000:6.1f}ms  "
              f"段频率 {freq:6.1f}  吞吐 {len(kept)/span*60 if span else 0:6.1f} 条/分")

    conns = ["单连接", "双连接"]

    print("\n===== 频率矩阵 口径A：吞吐率 (有效帧数/有效时长, 条/分) =====")
    print("物理状态 \\ 连接".ljust(12) + "".join(c.ljust(18) for c in conns))
    for g in MATRIX_GROUPS:
        line = g.ljust(12)
        for c in conns:
            v = throughput(seg_stats_map.get((g, c)))
            line += (f"{v:10.1f}条/分" if v is not None else "—").ljust(18)
        print(line)

    print("\n===== 频率矩阵 口径B：典型发射频率 (段中位间隔的中位数, 条/分) =====")
    print("物理状态 \\ 连接".ljust(12) + "".join(c.ljust(18) for c in conns))
    for g in MATRIX_GROUPS:
        line = g.ljust(12)
        for c in conns:
            vals = [m for _, m in seg_freq.get((g, c), [])]
            v = typical(vals)
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
            print(f"  {g} × {c}: 段数={len(freqs)}  段频率={['%.1f' % f for f in freqs]}  "
                  f"吞吐={throughput(seg_stats_map.get(key)):.1f}  典型={typical([m for _, m in seg_freq[key]]):.1f} 条/分")
            for (st, en, dur, n, bat) in seg_meta[key]:
                print(f"        {st}~{en}  {dur:5.1f}s  帧{n:4d}  电量 左={bat[0]} 右={bat[1]} 盒={bat[2]}")

    print("\n===== 同一物理状态下 单连接 vs 双连接 对比（两种口径）=====")
    for g in MATRIX_GROUPS:
        s_tp = throughput(seg_stats_map.get((g, "单连接")))
        d_tp = throughput(seg_stats_map.get((g, "双连接")))
        s_ty = typical([m for _, m in seg_freq.get((g, "单连接"), [])])
        d_ty = typical([m for _, m in seg_freq.get((g, "双连接"), [])])
        if None in (s_tp, d_tp, s_ty, d_ty):
            print(f"  {g}: 数据不全")
            continue
        r_tp = d_tp / s_tp
        verdict = "双连接更高" if r_tp > 1.15 else ("双连接更低" if r_tp < 0.85 else "基本持平(差异在噪声内)")
        print(f"  {g:<10} 吞吐率 单 {s_tp:6.1f} → 双 {d_tp:6.1f} ({r_tp:4.2f}x)   "
              f"典型频率 单 {s_ty:6.1f} → 双 {d_ty:6.1f} ({d_ty/s_ty:4.2f}x)   {verdict}")

def run_window_mode(rows, args):
    events = [t2s(x) for x in args.events.split(",") if x.strip()]
    offs = [t2s(x) for x in args.offs.split(",") if x.strip()] if args.offs.strip() else []
    windows = []
    for w in args.windows.split(","):
        if "-" in w:
            a, b = w.split("-")
            windows.append((float(a), float(b)))
        else:
            windows.append((0.0, float(w)))
    maxw = max(b for _, b in windows)

    print(f"===== 双连接建立期窗口分析 =====")
    print(f"事件数={len(events)}  窗口(相对开蓝牙时刻, 秒)={args.windows}")

    # 基线：不属于任何事件窗口的帧
    base_rows = []
    win_rows = {i: {w: [] for w in windows} for i in range(len(events))}
    for r in rows:
        placed = False
        for i, e in enumerate(events):
            if e <= r["_t"] < e + maxw:
                for w in windows:
                    if e + w[0] <= r["_t"] < e + w[1]:
                        win_rows[i][w].append(r)
                        placed = True
                break
        if not placed:
            base_rows.append(r)

    def stats_of(rs):
        if len(rs) < 3:
            return None
        span = rs[-1]["_t"] - rs[0]["_t"]
        if span <= 0:
            return None
        intervals = [rs[i]["_t"] - rs[i - 1]["_t"] for i in range(1, len(rs))]
        intervals = [d for d in intervals if d > 0]
        if not intervals:
            return None
        return {
            "frames": len(rs),
            "span": span,
            "tp": len(rs) / span * 60,
            "ty": 1 / median(intervals) * 60,
            "med_ms": median(intervals) * 1000,
            "L": sorted(set(x["Left"] for x in rs)),
            "R": sorted(set(x["Right"] for x in rs)),
            "C": sorted(set(x["Case"] for x in rs)),
        }

    base = stats_of(base_rows)
    print("\n--- 基线（单连接，事件窗口之外的所有帧）---")
    if base:
        print(f"  帧数={base['frames']}  时长={base['span']:.1f}s  "
              f"吞吐={base['tp']:.1f} 条/分  典型={base['ty']:.1f} 条/分  中位间隔={base['med_ms']:.0f}ms  "
              f"电量 左={base['L']} 右={base['R']} 盒={base['C']}")
    else:
        print("  无数据")

    for i, e in enumerate(events):
        print(f"\n--- 事件 {i+1}：开蓝牙于 {args.events.split(',')[i].strip()} ---")
        for w in windows:
            s = stats_of(win_rows[i][w])
            if not s:
                print(f"  窗口 {w[0]:.0f}-{w[1]:.0f}s : 无数据")
                continue
            if base:
                print(f"  窗口 {w[0]:.0f}-{w[1]:.0f}s : 帧数={s['frames']:4d} 时长={s['span']:5.1f}s  "
                      f"吞吐={s['tp']:6.1f} ({s['tp']/base['tp']:4.2f}x基线)  "
                      f"典型={s['ty']:6.1f} ({s['ty']/base['ty']:4.2f}x基线)  中位间隔={s['med_ms']:.0f}ms  "
                      f"电量 左={s['L']} 右={s['R']} 盒={s['C']}")
            else:
                print(f"  窗口 {w[0]:.0f}-{w[1]:.0f}s : 帧数={s['frames']:4d} 时长={s['span']:5.1f}s  "
                      f"吞吐={s['tp']:6.1f}  典型={s['ty']:6.1f}  中位间隔={s['med_ms']:.0f}ms")
        if i < len(offs):
            off = offs[i]
            tail = [r for r in rows if off <= r["_t"] < off + 60]
            s = stats_of(tail)
            if s:
                print(f"  [参考] 关蓝牙后 0-60s : 帧数={s['frames']:4d} 吞吐={s['tp']:6.1f} "
                      f"典型={s['ty']:6.1f} 中位间隔={s['med_ms']:.0f}ms")

if __name__ == "__main__":
    main()
