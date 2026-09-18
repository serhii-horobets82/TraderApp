#!/usr/bin/env bash
# First-time VPS setup. Run once, as a normal user with sudo:
#   bash deploy/install.sh
set -euo pipefail

if [[ "$(id -u)" -eq 0 ]]; then
  echo "run as a normal user with sudo, not as root"
  exit 1
fi

sudo apt-get update -y
sudo apt-get install -y rsync systemd-timesyncd

# The nonce is a timestamp the signature covers, so the clock has to be right.
sudo timedatectl set-ntp true || true

sudo mkdir -p /opt/trader/app /opt/trader/incoming /etc/trader
sudo chown -R "$USER:$USER" /opt/trader

if [[ ! -f /etc/trader/env ]]; then
  sudo tee /etc/trader/env >/dev/null <<'EOF'
# Credentials. Readable only by root; systemd loads it for the service.
WHITEBIT_API_KEY=
WHITEBIT_API_SECRET=
TELEGRAM_BOT_TOKEN=
TELEGRAM_CHAT_ID=

# Start in dry-run. Set to false only once you have watched it behave here.
# This wins over appsettings.json, so it is the safe switch to reach for.
TRADER_Trading__DryRun=true
EOF
  sudo chmod 600 /etc/trader/env
  sudo chown root:root /etc/trader/env
  echo "created /etc/trader/env - fill in the keys before starting"
fi

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
sudo cp "$SCRIPT_DIR/trader.service" /etc/systemd/system/trader.service

# Logs go to the journal; the app also writes its own daily files. Cap both so a
# long-running bot cannot fill the disk and take itself down.
sudo tee /etc/logrotate.d/trader >/dev/null <<'EOF'
/opt/trader/app/logs/*.log {
    daily
    rotate 14
    compress
    missingok
    notifempty
    copytruncate
}
EOF
sudo mkdir -p /etc/systemd/journald.conf.d
sudo tee /etc/systemd/journald.conf.d/trader.conf >/dev/null <<'EOF'
[Journal]
SystemMaxUse=500M
EOF
sudo systemctl restart systemd-journald || true

# The deploy user needs exactly these, and nothing else, without a password.
sudo tee /etc/sudoers.d/trader >/dev/null <<EOF
$USER ALL=NOPASSWD: /bin/systemctl stop trader, /bin/systemctl start trader, /bin/systemctl restart trader, /bin/systemctl status trader, /bin/systemctl is-active trader, /bin/systemctl show trader *
EOF
sudo chmod 440 /etc/sudoers.d/trader
sudo visudo -c -f /etc/sudoers.d/trader

sudo systemctl daemon-reload
sudo systemctl enable trader

cat <<'EOF'

install done.

next:
  1. sudo nano /etc/trader/env          # keys, and leave DryRun=true for now
  2. push to main - GitHub Actions deploys and starts the service
  3. journalctl -u trader -f            # watch it
  4. when you trust it: set TRADER_Trading__DryRun=false, then
     sudo systemctl restart trader
EOF
