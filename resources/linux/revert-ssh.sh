#!/bin/bash
# WslRemoteSetup — WSL 안 SSH 설정 되돌리기 (root로 실행: wsl -d <distro> -u root)
#
# 사용: bash revert-ssh.sh --user <linux_user> --installation-id <guid>
# - 이 도구가 만든 drop-in(마커+설치 ID 일치)만 지운다.
# - 이 도구가 추가한 공개키(지문 기록 ~/.local/share/wsl-remote-setup/added-keys)만 authorized_keys에서 뺀다. 다른 키는 보존.
# - 이 도구가 켠 ssh.socket/ssh.service를 끄고 멈춘다(패키지 openssh-server는 지우지 않음).
# - 상태 파일 /var/lib/wsl-remote-setup/ssh.json에 되돌림 시각을 남긴다.
set -euo pipefail
die() { echo "[FAIL] $*" >&2; exit 1; }
MARKER="# Managed by WslRemoteSetup"
DROPIN="/etc/ssh/sshd_config.d/10-wsl-remote-setup.conf"
STATE="/var/lib/wsl-remote-setup/ssh.json"

USER_NAME=""; INSTALL_ID=""
while [ $# -gt 0 ]; do
  case "$1" in
    --user) USER_NAME="${2:-}"; shift 2 ;;
    --installation-id) INSTALL_ID="${2:-}"; shift 2 ;;
    *) die "알 수 없는 인수: $1" ;;
  esac
done
[ "$(id -u)" -eq 0 ] || die "root로 실행해야 합니다."
[[ "$USER_NAME" =~ ^[a-z_][a-z0-9_-]{0,31}$ ]] && [ "$USER_NAME" != root ] || die "사용자 이름 오류"
[[ "$INSTALL_ID" =~ ^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$ ]] || die "installation-id 형식 오류"

# 1. drop-in
if [ -f "$DROPIN" ]; then
  if head -n 1 "$DROPIN" | grep -qF "$MARKER" && grep -qF "installationId $INSTALL_ID" "$DROPIN"; then
    rm -f "$DROPIN"; echo "DROPIN_REMOVED"
  else
    echo "DROPIN_FOREIGN_KEPT"
  fi
else
  echo "DROPIN_ABSENT"
fi

# 2. 이 도구가 추가한 공개키만 제거
HOME_DIR="$(getent passwd "$USER_NAME" | cut -d: -f6 || true)"
REC="$HOME_DIR/.local/share/wsl-remote-setup/added-keys"
AK="$HOME_DIR/.ssh/authorized_keys"
removed=0
if [ -n "$HOME_DIR" ] && [ -f "$REC" ] && [ -f "$AK" ]; then
  mapfile -t FPS < <(awk -v id="$INSTALL_ID" '$2==id {print $4}' "$REC")
  if [ "${#FPS[@]}" -gt 0 ]; then
    TMP="$(mktemp)"
    while IFS= read -r line || [ -n "$line" ]; do
      keep=1
      if [[ -n "$line" && "$line" != \#* ]]; then
        fp="$(printf '%s\n' "$line" | ssh-keygen -lf - 2>/dev/null | awk '{print $2}' || true)"
        for f in "${FPS[@]}"; do [ "$fp" = "$f" ] && keep=0; done
      fi
      if [ "$keep" -eq 1 ]; then printf '%s\n' "$line" >> "$TMP"; else removed=$((removed+1)); fi
    done < "$AK"
    cat "$TMP" > "$AK"; rm -f "$TMP"
    chown "$USER_NAME": "$AK"; chmod 0600 "$AK"
    awk -v id="$INSTALL_ID" '$2!=id' "$REC" > "$REC.tmp" && mv -f "$REC.tmp" "$REC" && chown "$USER_NAME": "$REC"
  fi
fi
echo "KEYS_REMOVED=$removed"

# 3. 서비스 끄기(패키지는 유지)
for u in ssh.socket ssh.service; do
  if systemctl cat "$u" >/dev/null 2>&1; then
    systemctl disable --now "$u" >/dev/null 2>&1 || true
  fi
done
if ss -ltnH | awk '$4 ~ /:22$/ {f=1} END {exit !f}'; then echo "LISTENER_STILL_PRESENT"; else echo "SSH_STOPPED"; fi

# 4. 기록
if [ -f "$STATE" ]; then
  sed -i -E 's/"updatedAt": "[^"]*"/"updatedAt": "'"$(date -u +%Y-%m-%dT%H:%M:%SZ)"'", "revertedAt": "'"$(date -u +%Y-%m-%dT%H:%M:%SZ)"'"/' "$STATE"
fi
PRE="$(grep -o '"opensshServerPreinstalled": [a-z]*' "$STATE" 2>/dev/null | awk '{print $2}' || true)"
echo "OPENSSH_PREINSTALLED=${PRE:-unknown}"
echo "[PASS] SSH 설정을 되돌렸습니다(패키지 openssh-server는 남겨 둠)."
