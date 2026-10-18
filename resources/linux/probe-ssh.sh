#!/bin/bash
# WslRemoteSetup — SSH 관련 읽기 전용 진단. root 권한·설정 변경 없음.
set -u
echo "== user: $(id -un) uid=$(id -u)"
echo "== os: $(. /etc/os-release; echo "$PRETTY_NAME")"
echo "== openssh-server:"
dpkg-query -W -f='${Status} ${Version}\n' openssh-server 2>/dev/null || echo "not installed"
echo "== openssh-client: $(dpkg-query -W -f='${Version}' openssh-client 2>/dev/null || echo none)"
echo "== candidate: $(apt-cache policy openssh-server 2>/dev/null | awk '/Candidate:/{print $2}')"
echo "== sshd_config present: $( [ -f /etc/ssh/sshd_config ] && echo yes || echo no)"
if [ -f /etc/ssh/sshd_config ]; then
  echo "== Include lines:"; grep -nE '^[[:space:]]*Include' /etc/ssh/sshd_config || echo "(none)"
fi
echo "== sshd_config.d:"; ls -la /etc/ssh/sshd_config.d 2>/dev/null || echo "(absent)"
echo "== host keys:"; ls -1 /etc/ssh/ssh_host_*_key.pub 2>/dev/null || echo "(none)"
echo "== units:"
for u in ssh.socket ssh.service; do
  printf '%s enabled=%s active=%s\n' "$u" "$(systemctl is-enabled "$u" 2>/dev/null || true)" "$(systemctl is-active "$u" 2>/dev/null || true)"
done
echo "== listeners :22"; ss -ltnH 2>/dev/null | awk '$4 ~ /:22$/' || true
echo "== ~/.ssh:"; ls -la "$HOME/.ssh" 2>/dev/null || echo "(absent)"
echo "== authorized_keys lines: $( [ -f "$HOME/.ssh/authorized_keys" ] && grep -cvE '^\s*(#|$)' "$HOME/.ssh/authorized_keys" || echo 0)"
echo "== tool state: $( [ -e /var/lib/wsl-remote-setup ] && ls /var/lib/wsl-remote-setup || echo none)"
echo "== apt reachability:"; curl -sS -o /dev/null -w '%{http_code}\n' --max-time 15 http://archive.ubuntu.com/ubuntu/dists/noble/Release 2>&1
