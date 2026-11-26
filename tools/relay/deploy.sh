#!/usr/bin/env bash
# Installs or updates tt-relay on the server. Run as root from the upload folder:
#   bash deploy.sh tt.example.com
# Safe to run again: config.json and keys.json are never overwritten once they exist.
set -euo pipefail

DOMAIN="${1:?usage: bash deploy.sh <subdomain>}"
HERE="$(cd "$(dirname "$0")" && pwd)"
SERVER_IP="139.180.218.58"
STAMP="$(date +%Y%m%dT%H%M%S)"
BACKUP="/root/tt-relay-backup-$STAMP"

echo "== 1. 检查域名解析"
RESOLVED="$(getent ahostsv4 "$DOMAIN" | awk 'NR==1{print $1}' || true)"
if [[ "$RESOLVED" != "$SERVER_IP" ]]; then
  echo "域名 $DOMAIN 解析到 '${RESOLVED:-无}'，不是 $SERVER_IP。请先加好 A 记录再运行。" >&2
  exit 1
fi

echo "== 2. 记录现有网站状态并备份 Apache 配置到 $BACKUP"
mkdir -p "$BACKUP"
tar -czf "$BACKUP/apache2.tar.gz" -C /etc apache2
SITES="$(grep -RhoiE '^\s*ServerName\s+\S+' /etc/apache2/sites-enabled/ | awk '{print $2}' | sort -u | grep -vx "$DOMAIN" || true)"
for s in $SITES; do
  printf '%s %s\n' "$s" "$(curl -s -o /dev/null -w '%{http_code}' --max-time 15 "https://$s/" || echo ERR)"
done > "$BACKUP/sites-before.txt"
cat "$BACKUP/sites-before.txt"

echo "== 3. 安装程序（专用账户 tt-relay，不用 root 运行）"
id tt-relay >/dev/null 2>&1 || useradd --system --no-create-home --shell /usr/sbin/nologin tt-relay
install -d -m 755 -o root -g root /opt/tt-relay
install -m 755 -o root -g root "$HERE/tt_relay.py" /opt/tt-relay/tt_relay.py
install -d -m 750 -o root -g tt-relay /etc/tt-relay
if [[ ! -f /etc/tt-relay/config.json ]]; then
  install -m 640 -o root -g tt-relay "$HERE/config.example.json" /etc/tt-relay/config.json
fi
if [[ ! -f /etc/tt-relay/keys.json ]]; then
  printf '{\n  "openai": "",\n  "deepseek": ""\n}\n' > /etc/tt-relay/keys.json
  chown root:tt-relay /etc/tt-relay/keys.json
  chmod 640 /etc/tt-relay/keys.json
fi
install -m 644 -o root -g root "$HERE/tt-relay.service" /etc/systemd/system/tt-relay.service
systemctl daemon-reload
systemctl enable tt-relay >/dev/null
systemctl restart tt-relay
sleep 1
curl -fsS --max-time 5 http://127.0.0.1:8230/healthz && echo " 转发程序已启动"

echo "== 4. 新增 Apache 网站 $DOMAIN（不改动现有网站）"
# Only the first time: certbot later adds its redirect to this file, and a rerun must keep it.
if [[ ! -f /etc/apache2/sites-available/tt-relay.conf ]]; then
  sed "s/tt\.example\.com/$DOMAIN/g" "$HERE/apache-tt-relay.conf" > /etc/apache2/sites-available/tt-relay.conf
fi
a2enmod -q proxy proxy_http
a2ensite -q tt-relay
apache2ctl configtest
systemctl reload apache2

echo "== 5. 申请 HTTPS 证书"
if [[ ! -f /etc/apache2/sites-enabled/tt-relay-le-ssl.conf ]]; then
  certbot --apache -d "$DOMAIN" --redirect --non-interactive
fi
apache2ctl configtest
systemctl reload apache2
sleep 1
curl -fsS --max-time 10 "https://$DOMAIN/healthz" && echo " HTTPS 正常"

echo "== 6. 核对现有网站状态没有变化"
for s in $SITES; do
  printf '%s %s\n' "$s" "$(curl -s -o /dev/null -w '%{http_code}' --max-time 15 "https://$s/" || echo ERR)"
done > "$BACKUP/sites-after.txt"
if diff "$BACKUP/sites-before.txt" "$BACKUP/sites-after.txt"; then
  echo "现有网站状态一致。"
else
  echo "注意：上面有网站状态变了，请检查。回退方法见 DEPLOY.md。" >&2
fi

echo
echo "完成。备份在 $BACKUP。"
echo "接下来：nano /etc/tt-relay/keys.json 填入 Key，然后 python3 /opt/tt-relay/tt_relay.py add-friend 名字"
