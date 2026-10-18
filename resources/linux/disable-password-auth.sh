#!/bin/bash
# WslRemoteSetup — 공개키 인증 확인 후 SSH 비밀번호 로그인 해제 (FR-04)
#
# 사용: sudo bash disable-password-auth.sh --user <linux_user> --installation-id <guid> --confirmed-key SHA256:...
#
# 전제: 외부 클라이언트에서 해당 키로 공개키 로그인을 이미 확인했다(--confirmed-key).
# - 사용자 authorized_keys에 그 지문의 키가 없으면 중단한다(유일한 로그인 수단 제거 방지).
# - 도구 소유 drop-in만 수정하고, sshd -t / sshd -T 검증 실패 시 원복한다.
# - 기존 SSH 세션은 끊지 않는다(소켓 활성화: 새 연결부터 적용).
set -euo pipefail
MARKER="# Managed by WslRemoteSetup"
DROPIN="/etc/ssh/sshd_config.d/10-wsl-remote-setup.conf"
STATE="/var/lib/wsl-remote-setup/ssh.json"
die() { echo "[FAIL] $*" >&2; exit 1; }

USER_NAME=""; INSTALL_ID=""; KEY_FP=""
while [ $# -gt 0 ]; do
  case "$1" in
    --user) USER_NAME="${2:-}"; shift 2 ;;
    --installation-id) INSTALL_ID="${2:-}"; shift 2 ;;
    --confirmed-key) KEY_FP="${2:-}"; shift 2 ;;
    *) die "알 수 없는 인수: $1" ;;
  esac
done
[ "$(id -u)" -eq 0 ] || die "sudo로 실행해야 합니다."
[[ "$USER_NAME" =~ ^[a-z_][a-z0-9_-]{0,31}$ ]] && [ "$USER_NAME" != root ] || die "사용자 이름 오류"
[[ "$INSTALL_ID" =~ ^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$ ]] || die "installation-id 형식 오류"
[[ "$KEY_FP" =~ ^SHA256:[A-Za-z0-9+/]{43}$ ]] || die "--confirmed-key 형식 오류"
[ -f "$DROPIN" ] && head -n 1 "$DROPIN" | grep -qF "$MARKER" || die "도구 소유 drop-in이 없습니다."
grep -qF "installationId $INSTALL_ID" "$DROPIN" || die "drop-in의 설치 ID가 다릅니다."

HOME_DIR="$(getent passwd "$USER_NAME" | cut -d: -f6)"
AK="$HOME_DIR/.ssh/authorized_keys"
[ -f "$AK" ] || die "$AK 없음 — 비밀번호를 끄면 로그인할 수 없습니다."
ssh-keygen -lf "$AK" 2>/dev/null | awk '{print $2}' | grep -qx "$KEY_FP" \
  || die "확인한 키($KEY_FP)가 authorized_keys에 없습니다. 변경하지 않았습니다."

BACKUP="$(mktemp)"; cp -p "$DROPIN" "$BACKUP"
restore() { cp -p "$BACKUP" "$DROPIN"; }
sed -i -E 's/^PasswordAuthentication[[:space:]]+yes$/PasswordAuthentication no/' "$DROPIN"
sed -i -E 's/^# 공개키 인증을 외부 클라이언트에서 확인하기 전까지.*$/# 공개키 인증 확인 완료('"$KEY_FP"'). 비밀번호 로그인 해제./' "$DROPIN"
grep -qx "PasswordAuthentication no" "$DROPIN" || { restore; die "drop-in 수정 실패"; }

install -d -m 0755 /run/sshd
sshd -t || { restore; die "sshd -t 실패. 원복했습니다."; }
EFF="$(sshd -T -C "user=$USER_NAME,host=lan-client,addr=192.0.2.10")"
for want in "passwordauthentication no" "kbdinteractiveauthentication no" "pubkeyauthentication yes" "permitrootlogin no" "allowusers $USER_NAME"; do
  grep -qx "$want" <<< "$EFF" || { restore; die "유효값 불일치: $want. 원복했습니다."; }
  echo "  $want"
done
rm -f "$BACKUP"

if [ -f "$STATE" ]; then
  sed -i -E 's/"passwordAuthentication": true/"passwordAuthentication": false/; s/"updatedAt": "[^"]*"/"updatedAt": "'"$(date -u +%Y-%m-%dT%H:%M:%SZ)"'"/' "$STATE"
fi
echo "[PASS] 비밀번호 로그인을 해제했습니다. 새 연결부터 공개키만 허용합니다(기존 세션 유지)."
echo "되돌리기: drop-in의 'PasswordAuthentication no'를 yes로 바꾸는 동일 절차(도구 재실행)."
