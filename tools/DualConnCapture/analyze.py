#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""双连接对照分析 V2: 用相邻帧间隔分离'物理状态(在盒/取出)'与'连接数(单/双)'两个变量。"""
import csv
from datetime import datetime
from statistics import median

CSV = r"d:\code\wireless-battery\testdata\dualconn-capture.csv"

def t2s(s: str) -> float:
    h, m, rest = s.split(":")
    return int(h) * 3600 + int(m) * 60 + float(rest)

# 用户操作时刻 (墙上时钟): A 单(手机关,取出) 13:16-13:20 | B 双(手机开) 13:20-13:23 | C 单(手机关) 13:23+
PHASE_CONN = [(t2s("13:16:00"), t2s("13:20:00"), "单"),
              (t2s("13:20:00"), t2s("13:23:00"), "双"),
              (t2s("13:23:00"), t2s("13:40:00"), "单")]

rows = []
with open(CSV, encoding="utf-8-sig", newline="") as f:
    for r in csv.DictReader(f):
        r["_t"] = t2s(r["LocalTime"])
        r["Rssi"] = int(r["Rssi"])
        for k in ("Left", "Right", "Case", "StateByte", "Connected",
                  "LeftInCase", "RightInCase", "BaseClosed"):
            try:
                r[k] = int(r[k], 0) if isinstance(r[k], str) and r[k].startswith("0x") else int(r[k])
            except (ValueError, TypeError):
                pass
        rows.append(r)

print(f"总记录数: {len(rows)}  范围: {rows[0]['LocalTime']} -> {rows[-1]['LocalTime']}")

# 逐帧: 相邻间隔 -> 瞬时频率(条/秒), 并标记 (物理状态, 连接)
def phase_conn(t):
    for t0, t1, c in PHASE_CONN:
        if t0 <= t < t1:
            return c
    return "单"

prev = None
recs = []  # (in_case, conn, rate, left, right, case)
for r in rows:
    if prev:
        dt = r["_t"] - prev["_t"]
        if dt > 0:
            in_case = (r["LeftInCase"] == 1 or r["RightInCase"] == 1)
            recs.append((in_case, phase_conn(r["_t"]), 1.0 / dt,
                         r["Left"], r["Right"], r["Case"]))
    prev = r

def summarize(label, sub):
    n = len(sub)
    if not n:
        print(f"  {label}: 无样本")
        return
    rates = [x[2] for x in sub]
    md = median(rates)
    print(f"  {label}: 样本={n}  间隔中位数={1/md:.2f}s  频率中位数={md:.2f}条/秒 ({md*60:.1f}条/分)")
    print(f"      左耳={sorted(set(x[3] for x in sub))} 右耳={sorted(set(x[4] for x in sub))} 盒={sorted(set(x[5] for x in sub))}")

print("\n===== 分层: 物理状态 × 连接数 =====")
print("-- 取出状态 (LeftInCase=0 且 RightInCase=0) --")
summarize("  取出+单连接 (A/C段取出部分)", [x for x in recs if not x[0] and x[1] == "单"])
summarize("  取出+双连接 (B段, 全程取出)",   [x for x in recs if not x[0] and x[1] == "双"])
print("-- 在盒状态 (任一耳机在盒内) --")
summarize("  在盒+单连接 (A开头 / C放回后)",  [x for x in recs if x[0] and x[1] == "单"])

# 各段整体频率 (含状态混合, 仅供参考)
print("\n===== 各段整体频率 (状态混合, 仅供参考) =====")
for t0, t1, c in PHASE_CONN:
    seg_rows = [r for r in rows if t0 <= r["_t"] < t1]
    n = len(seg_rows)
    dur = t1 - t0
    # 该段内在盒占比
    in_case_n = sum(1 for r in seg_rows if r["LeftInCase"] == 1 or r["RightInCase"] == 1)
    pct = 100.0 * in_case_n / n if n else 0
    print(f"  {c}连接 窗口 {t0/3600:02.0f}:{(t0%3600)//60:02.0f}-{t1/3600:02.0f}:{(t1%3600)//60:02.0f}: "
          f"记录={n} 整体频率={n/dur*60:.1f}条/分 在盒占比={pct:.0f}%")
