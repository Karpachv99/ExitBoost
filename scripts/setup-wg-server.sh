#!/usr/bin/env bash
# Подготовка VPS-узла для ExitBoost (WireGuard-релей).
#
# Что делает:
#   * устанавливает wireguard-tools;
#   * генерирует серверный и клиентский ключи;
#   * поднимает туннель wg0 с NAT ( masquerade ) и форвардингом;
#   * печатает готовые параметры для добавления узла в ExitBoost.
#
# Запуск:  sudo bash scripts/setup-wg-server.sh [подсеть_клиента]
# Пример:  sudo bash scripts/setup-wg-server.sh 10.13.0

set -euo pipefail

SUBNET="${1:-10.13.0}"
SERVER_IP="${SERVER_IP:-$(curl -fsS https://api.ipify.org)}"
SERVER_PORT="${SERVER_PORT:-51820}"
MTU="${MTU:-1420}"
CLIENT_NAME="${CLIENT_NAME:-exitboost}"

echo "== ExitBoost: подготовка узла =="
echo "Сервер: ${SERVER_IP}:${SERVER_PORT}, подсеть клиента ${SUBNET}.0/24"

if [[ $EUID -ne 0 ]]; then
  echo "Нужен root: запустите через sudo" >&2
  exit 1
fi

if ! command -v wg >/dev/null 2>&1; then
  echo "-- установка wireguard"
  if command -v apt-get >/dev/null 2>&1; then
    apt-get update -y && apt-get install -y wireguard
  elif command -v dnf >/dev/null 2>&1; then
    dnf install -y wireguard-tools
  elif command -v yum >/dev/null 2>&1; then
    yum install -y wireguard-tools
  else
    echo "Установите wireguard-tools вручную и повторите" >&2
    exit 1
  fi
fi

echo "-- генерация ключей"
SERVER_PRIV=$(wg genkey)
SERVER_PUB=$(echo "$SERVER_PRIV" | wg pubkey)
CLIENT_PRIV=$(wg genkey)
CLIENT_PUB=$(echo "$CLIENT_PRIV" | wg pubkey)

mkdir -p /etc/wireguard
chmod 700 /etc/wireguard

cat > /etc/wireguard/wg0.conf <<EOF
[Interface]
Address = ${SUBNET}.1/24
ListenPort = ${SERVER_PORT}
PrivateKey = ${SERVER_PRIV}
PostUp = iptables -t nat -A POSTROUTING -s ${SUBNET}.0/24 -o eth0 -j MASQUERADE
PostUp = iptables -A FORWARD -i wg0 -j ACCEPT
PostDown = iptables -t nat -D POSTROUTING -s ${SUBNET}.0/24 -o eth0 -j MASQUERADE
PostDown = iptables -D FORWARD -i wg0 -j ACCEPT

[Peer]
# Клиент ${CLIENT_NAME}
PublicKey = ${CLIENT_PUB}
AllowedIPs = ${SUBNET}.2/32
EOF
chmod 600 /etc/wireguard/wg0.conf

echo "-- включение форвардинга"
sysctl -w net.ipv4.ip_forward=1 >/dev/null
grep -q 'net.ipv4.ip_forward=1' /etc/sysctl.conf 2>/dev/null || echo 'net.ipv4.ip_forward=1' >> /etc/sysctl.conf

echo "-- запуск службы"
if command -v systemctl >/dev/null 2>&1; then
  systemctl enable --now wg-quick@wg0
  systemctl status wg-quick@wg0 --no-pager | head -n 5 || true
else
  wg-quick up wg0
fi

echo
echo "================ ПАРАМЕТРЫ УЗЛА ДЛЯ EXITBOOST ================"
echo "Название      : ${CLIENT_NAME}-$(hostname -s)"
echo "Адрес         : ${SERVER_IP}"
echo "Порт          : ${SERVER_PORT}"
echo "Приватный ключ (клиент): ${CLIENT_PRIV}"
echo "Публичный ключ (сервер): ${SERVER_PUB}"
echo "Локальный адрес       : ${SUBNET}.2/32"
echo "MTU                  : ${MTU}"
echo "==========================================================="
echo
echo "Добавьте эти значения в ExitBoost → «Добавить узел» (тип WireGuard)."
