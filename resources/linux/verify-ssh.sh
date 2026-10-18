#!/bin/bash
# WslRemoteSetup — SSH 구성 사후 검증(읽기 전용, root 불필요)
set -u
echo "== drop-in:"; cat /etc/ssh/sshd_config.d/10-wsl-remote-setup.conf 2>&1
echo "== other drop-ins:"; ls -1 /etc/ssh/sshd_config.d/
echo "== state:"; cat /var/lib/wsl-remote-setup/ssh.json 2>&1
echo "== units: socket=$(systemctl is-active ssh.socket) service=$(systemctl is-active ssh.service)"
echo "== listeners:"; ss -ltnH | awk '$4 ~ /:22$/ {print "  " $4}'
echo "== banner (127.0.0.1:22):"
exec 3<>/dev/tcp/127.0.0.1/22 && { timeout 5 head -n 1 <&3; exec 3>&-; } || echo "connect failed"
echo "== wsl ip:"; ip -4 -o addr show dev eth0 | awk '{print "  " $4}'
