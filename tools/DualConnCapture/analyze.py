#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""双连接 × 物理状态 频率矩阵分析。
物理状态分四档：双耳在盒 / 双耳离盒 / 仅左在盒 / 仅右在盒；连接分单/双（由手机蓝牙开/关时刻界定）。
用法:
  python analyze.py [--csv 路径] [--on 手机开时刻 HH:MM:SS] [--off 手机关时刻 HH:MM:SS]
默认沿用上次实验时刻: 手机开 13:20:00, 手机关 13:23:00。
频率用相邻帧间隔中位数（抗突发/静默污染），电量报取值集合以验证可读性。
"""
import csv
import argparse
from statistics import median

def t2s(s: str) -> float:
    h, m, rest = s.split(":")
    return int(h) * 3600 + int(m) * 60 + float(rest)

def phys_state(r) -> str:
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

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--csv", default=r"d:\code\wireless-battery\testdata\dualconn-capture.csv")
    ap.add_argument("--on", default="13:20:00", help="手机蓝牙开启(进入双连接)时刻")
    ap.add_argument("--off", default="13:23:00", help="手机蓝牙关闭(回到单连接)时刻")
    args = ap.parse_args()

    ton, toff = t2s(args.on), t2s(args.off)
    rows = []
    with open(args.csv, encoding="utf-8-sig", newline="") as f:
        for r in csv.DictReader(f):
            r["_t"] = t2s(r["LocalTime"])
            for k in ("Left", "Right", "Case", "LeftInCase", "RightInCase"):
                try:
                    r[k] = int(r[k], 0) if isinstance(r[k], str) and r[k].startswith("0x") else int(r[k])
                except (ValueError, TypeError):
                    pass
            rows.append(r)

    print(f"总记录: {len(rows)}  手机开(双连接)窗口: {args.on}–{args.off}")

    # 逐帧相邻间隔 -> 瞬时频率, 标记 (物理组, 连接)
    prev = None
    recs = []
    for r in rows:
        if prev:
            dt = r["_t"] - prev["_t"]
            if dt > 0:
                conn = "双连接" if ton <= r["_t"] < toff else "单连接"
                recs.append((phys_group(r), conn, 1.0 / dt,
                             r["Left"], r["Right"], r["Case"]))
        prev = r

    groups = ["双耳在盒", "单耳在盒", "双耳离盒"]
    conns = ["单连接", "双连接"]
    print("\n===== 频率矩阵 (条/分, 间隔中位数) =====")
    header = "物理状态 \\ 连接".ljust(12) + "".join(c.ljust(14) for c in conns)
    print(header)
    for g in groups:
        line = g.ljust(12)
        for c in conns:
            sub = [x for x in recs if x[0] == g and x[1] == c]
            if sub:
                md = median(x[2] for x in sub)
                line += f"{md*60:8.1f}条/分".ljust(14)
            else:
                line += "—".ljust(14)
        print(line)

    print("\n===== 各组合明细 (样本数 / 频率 / 电量集合) =====")
    for g in groups:
        for c in conns:
            sub = [x for x in recs if x[0] == g and x[1] == c]
            if not sub:
                print(f"  {g} × {c}: 无样本")
                continue
            md = median(x[2] for x in sub)
            L = sorted(set(x[3] for x in sub))
            R = sorted(set(x[4] for x in sub))
            C = sorted(set(x[5] for x in sub))
            print(f"  {g} × {c}: 样本={len(sub):4d}  频率={md*60:6.1f}条/分  "
                  f"左={L} 右={R} 盒={C}")

if __name__ == "__main__":
    main()
