#!/usr/bin/env bash
# FlashForge Monitor installer for Raspberry Pi OS / Debian / Ubuntu (systemd).
#   curl -fsSL https://raw.githubusercontent.com/dougkl/flashforge-monitor/main/deploy/install.sh | sudo bash -s -- 192.168.1.50
# Re-run it any time to update to the latest release (your settings are kept).
set -euo pipefail

REPO="dougkl/flashforge-monitor"
DIR="/opt/flashforge-monitor"
PRINTER_IP="${1:-}"

[[ $EUID -eq 0 ]] || { echo "Please run as root (sudo)." >&2; exit 1; }

case "$(uname -m)" in
  x86_64)        RID="linux-x64" ;;
  aarch64|arm64) RID="linux-arm64" ;;
  armv7l|armv6l) RID="linux-arm" ;;
  *) echo "Unsupported CPU: $(uname -m)" >&2; exit 1 ;;
esac

echo "==> Finding latest release for $RID"
URL=$(curl -fsSL "https://api.github.com/repos/$REPO/releases/latest" \
  | grep -o "https://[^\"]*flashforge-monitor-$RID\.tar\.gz" | head -n1)
[[ -n "$URL" ]] || { echo "No release asset found for $RID" >&2; exit 1; }

echo "==> Downloading $URL"
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT
curl -fsSL "$URL" -o "$TMP/ffm.tar.gz"

id -u ffmonitor >/dev/null 2>&1 || useradd --system --no-create-home --shell /usr/sbin/nologin ffmonitor

systemctl stop flashforge-monitor 2>/dev/null || true
mkdir -p "$DIR"
tar -xzf "$TMP/ffm.tar.gz" -C "$DIR"
chmod +x "$DIR/flashforge-monitor"

if [[ ! -f "$DIR/appsettings.local.json" ]]; then
  if [[ -z "$PRINTER_IP" ]]; then
    read -rp "Printer IP address: " PRINTER_IP </dev/tty
  fi
  cat > "$DIR/appsettings.local.json" <<EOF
{
  "Printer": { "Host": "$PRINTER_IP", "CheckCode": "" },
  "Monitor": { "Password": "" },
  "Notifications": { "Webhooks": [] }
}
EOF
fi
chown -R ffmonitor:ffmonitor "$DIR"
chmod 600 "$DIR/appsettings.local.json"

curl -fsSL "https://raw.githubusercontent.com/$REPO/main/deploy/flashforge-monitor.service" \
  -o /etc/systemd/system/flashforge-monitor.service
systemctl daemon-reload
systemctl enable --now flashforge-monitor

IP=$(hostname -I 2>/dev/null | awk '{print $1}')
echo
echo "==> FlashForge Monitor is running: http://${IP:-<this-machine>}:5080"
echo "    Settings: $DIR/appsettings.local.json   Logs: journalctl -u flashforge-monitor -f"
