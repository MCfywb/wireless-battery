#!/usr/bin/env bash
# 把 plugins/vivo-tws-3i 组装成宿主可发现的插件包，输出到 artifacts/plugins/vivo-tws-3i/
# 布局与 docs/development-plan.md §5.1 一致：manifest.json + worker/ + profiles/。
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC="$ROOT/plugins/vivo-tws-3i"
OUT="$ROOT/artifacts/plugins/vivo-tws-3i"

if [ ! -d "$SRC" ]; then
  echo "找不到插件源目录：$SRC" >&2
  exit 1
fi

echo "==> 构建 Worker"
dotnet build "$SRC/worker/VivoTws3i.Worker.csproj" -c Release --nologo -v q

echo "==> 组装插件包到 $OUT"
rm -rf "$OUT"
mkdir -p "$OUT/worker" "$OUT/profiles"

cp "$SRC/manifest.json" "$OUT/manifest.json"
cp "$SRC/profile.json" "$OUT/profiles/default.json"
if [ -f "$SRC/README.md" ]; then cp "$SRC/README.md" "$OUT/README.md"; fi

# Worker 的全部输出（含依赖 dll / runtimeconfig / profile.json）复制到 worker/ 目录。
cp -r "$SRC/worker/bin/Release/net10.0-windows10.0.26100.0/." "$OUT/worker/"

echo "==> 插件包内容："
find "$OUT" -maxdepth 2 -type f | sed "s|$OUT|  <plugin>|"
echo "==> 完成。宿主插件目录：$OUT"
