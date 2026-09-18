#!/usr/bin/env bash
# First-time VPS setup. Run as the deploy user (ubuntu / debian).
#   curl -fsSL ... | bash   is not used — copy this repo to the box or:
#   bash deploy/install.sh
set -euo pipefail

if [[ "$(id -u)" -eq 0 ]]; then
  echo "run as a normal user with sudo, not as root"
  exit 1
fi

sudo apt-get update -y
sudo apt-get install -y rsync
sudo mkdir -p /opt/trader/app /opt/trader/incoming /etc/trader
sudo chown -R "$USER:$USER" /opt/trader

if [[ ! -f /etc/trader/env ]]; then
  sudo tee /etc/trader/env >/dev/null <<'EOF'
WHITEBIT_API_KEY=
WHITEBIT_API_SECRET=
TELEGRAM_BOT_TOKEN=
TELEGRAM_CHAT_ID=
EOF
  sudo chmod 600 /etc/trader/env
  sudo chown root:root /etc/trader/env
  echo "created /etc/trader/env — fill in the keys before starting"
fi

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
sudo cp "$SCRIPT_DIR/trader.service" /etc/systemd/system/trader.service

SUDOERS_LINE="$USER ALL=NOPASSWD: /bin/systemctl stop trader, /bin/systemctl start trader, /bin/systemctl restart trader, /bin/systemctl status trader, /bin/systemctl is-active trader"
echo "$SUDOERS_LINE" | sudo tee /etc/sudoers.d/trader >/dev/null
sudo chmod 440 /etc/sudoers.d/trader

sudo systemctl daemon-reload
sudo systemctl enable trader
echo "install done. After the first GitHub deploy (or a manual copy into /opt/trader/app): sudo systemctl start trader"
