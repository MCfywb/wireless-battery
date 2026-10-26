#!/usr/bin/env bash
# 把 plugins/vivo-tws-3i 组装成宿主可发现的插件包，输出到 artifacts/plugins/vivo-tws-3i/
# 布局与 docs/development-plan.md §5.1 一致：manifest.json + worker/ + profiles/。
#
# 默认同时「安装」到宿主真正会扫描的目录：
#   %LocalAppData%/PeripheralBatteryMonitor/plugins/vivo-tws-3i/
# 原因：宿主只在启动时扫描该目录一次，只组装到 artifacts/ 不会被发现，
# 表现为托盘里「尚未读取到设备」。用 --no-install 可跳过安装。
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC="$ROOT/plugins/vivo-tws-3i"
OUT="$ROOT/artifacts/plugins/vivo-tws-3i"
PLUGIN_ID="vivo-tws-3i"

DO_INSTALL=1
for arg in "$@"; do
  case "$arg" in
    --no-install) DO_INSTALL=0 ;;
    -h|--help)
      echo "用法：stage-plugin.sh [--no-install]"
      echo "  --no-install  只组装到 artifacts/plugins/，不安装到宿主插件目录"
      exit 0 ;;
    *)
      echo "未知参数：$arg" >&2
      exit 2 ;;
  esac
done

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

if [ "$DO_INSTALL" -eq 1 ]; then
  # 宿主默认插件目录（PluginCatalog.DefaultPluginsDirectory）。
  # Git Bash 下 LOCALAPPDATA 是 Windows 风格路径，需转成 POSIX 路径再复制。
  INSTALL_ROOT="${PERIPHERAL_BATTERY_PLUGINS_DIR:-}"
  if [ -z "$INSTALL_ROOT" ]; then
    if [ -z "${LOCALAPPDATA:-}" ]; then
      echo "无法定位 LOCALAPPDATA，跳过安装（如需手动安装请设置 PERIPHERAL_BATTERY_PLUGINS_DIR）" >&2
      INSTALL_ROOT=""
    elif command -v cygpath >/dev/null 2>&1; then
      INSTALL_ROOT="$(cygpath -u "$LOCALAPPDATA")/PeripheralBatteryMonitor/plugins"
    else
      INSTALL_ROOT="$LOCALAPPDATA/PeripheralBatteryMonitor/plugins"
    fi
  fi

  if [ -n "$INSTALL_ROOT" ]; then
    echo "==> 安装到宿主插件目录 $INSTALL_ROOT/$PLUGIN_ID"
    mkdir -p "$INSTALL_ROOT"
    rm -rf "$INSTALL_ROOT/$PLUGIN_ID"
    cp -r "$OUT" "$INSTALL_ROOT/$PLUGIN_ID"
    test -f "$INSTALL_ROOT/$PLUGIN_ID/manifest.json" \
      && test -f "$INSTALL_ROOT/$PLUGIN_ID/worker/VivoTws3i.Worker.exe" \
      || { echo "安装校验失败" >&2; exit 1; }
    echo "==> 安装完成。重启托盘程序后生效（插件发现只在启动时执行一次）。"
  fi
else
  echo "==> 已跳过安装（--no-install）。注意：宿主不会扫描 artifacts/，托盘将读不到设备。"
fi

echo "==> 完成。组装产物：$OUT"
