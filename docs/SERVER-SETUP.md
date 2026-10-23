# Подготовка своего узла (VPS) для ExitBoost

Кратко: ExitBoost отправляет трафик игры на ваш сервер, а сервер отправляет его
в интернет. Чем ближе сервер к игровому серверу, тем меньше задержка и потери.

## 1. Арендуйте VPS

Подойдёт любой дешёвый VPS (Hetzner, OVH, Timeweb, Selectel и т.п.):

- локация — рядом с игровым сервером (например, Франкфурт для европейских игр);
- 1 vCPU / 512 МБ ОЗУ / 10 ГБ диска достаточно;
- публичный IPv4;
- UDP-порт 51820 (или любой другой) свободен.

## 2. Поднимите WireGuard-релей

```bash
sudo bash scripts/setup-wg-server.sh 10.13.0
```

Скрипт установит `wireguard-tools`, сгенерирует ключи, включит NAT и выведет
готовые параметры.

### Вручную (если скрипт не подходит)

```bash
apt-get update && apt-get install -y wireguard

SERVER_PRIV=$(wg genkey); SERVER_PUB=$(echo "$SERVER_PRIV" | wg pubkey)
CLIENT_PRIV=$(wg genkey); CLIENT_PUB=$(echo "$CLIENT_PRIV" | wg pubkey)

cat > /etc/wireguard/wg0.conf <<EOF
[Interface]
Address = 10.13.0.1/24
ListenPort = 51820
PrivateKey = ${SERVER_PRIV}
PostUp = iptables -t nat -A POSTROUTING -s 10.13.0.0/24 -o eth0 -j MASQUERADE
PostUp = iptables -A FORWARD -i wg0 -j ACCEPT
PostDown = iptables -t nat -D POSTROUTING -s 10.13.0.0/24 -o eth0 -j MASQUERADE
PostDown = iptables -D FORWARD -i wg0 -j ACCEPT

[Peer]
PublicKey = ${CLIENT_PUB}
AllowedIPs = 10.13.0.2/32
EOF

chmod 600 /etc/wireguard/wg0.conf
sysctl -w net.ipv4.ip_forward=1
systemctl enable --now wg-quick@wg0
```

## 3. Добавьте узел в ExitBoost

В приложении: «Добавить» → тип **WireGuard**:

| Поле | Значение |
|---|---|
| Имя | любое, например `Frankfurt-1` |
| Страна | `Germany` |
| Адрес | IP вашего VPS |
| Порт | `51820` |
| Приватный ключ | `CLIENT_PRIV` (клиентский) |
| Публичный ключ пира | `SERVER_PUB` (серверный) |
| Локальный адрес | `10.13.0.2/32` |
| DNS | `1.1.1.1,1.0.0.1` |
| MTU | `1420` |

## 4. Проверьте

```powershell
ExitBoost probe
ExitBoost connect --best --mode full
```

Признак успеха — в журнале появляется `Туннель поднят`, а в таблице узлов
появляется ненулевой пинг и рукопожатие WireGuard.

## Несколько узлов

Скрипт можно запустить несколько раз с разными подсетями (10.13.0, 10.14.0, …),
и завести на одном VPS несколько интерфейсов `wg0`, `wg1`, … либо поднять
несколько VPS. Приложение измеряет все узлы и само выбирает лучший по
задержке, джиттеру и потерям.

## Что учесть

- Включите на VPS BBR: `apt-get install -y linux-modules-extra-$(uname -r)` и
  `net.core.default_qdisc = fq` + `net.ipv4.tcp_congestion_control = bbr`
  в `/etc/sysctl.conf` — заметно снижает потери.
- Не открывайте доступ по SSH для всех подряд; используйте ключи.
- Публичные IP игровых серверов меняются — режим «Только трафик выбранной игры»
  с автоопределением адресов справляется с этим сам (маршруты обновляются на лету).
