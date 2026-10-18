#!/bin/bash
# WslRemoteSetup — WSL OpenSSH 서버 설치·설정 (M4 / FR-04)
#
# 사용: sudo bash install-openssh.sh --user <linux_user> --installation-id <guid>
#
# 하는 일
#   1. openssh-server 설치(이미 있으면 유지)
#   2. 도구 소유 drop-in /etc/ssh/sshd_config.d/10-wsl-remote-setup.conf 작성
#      - 남이 만든 같은 이름 파일이 있으면 중단(덮어쓰지 않음)
#   3. sshd -t 구문 검사 → 실패 시 drop-in 원복 후 중단
#   4. sshd -T 유효값 검증(root 금지, 허용 사용자) → 불일치 시 원복 후 중단
#   5. ssh.socket(없으면 ssh.service) 사용·시작, TCP 22 리스너 확인
#   6. 소유 리소스 기록 /var/lib/wsl-remote-setup/ssh.json (비밀값 없음)
#
# 하지 않는 일: 기존 host key 교체, authorized_keys 변경, 비밀번호 변경, 방화벽·Windows 설정.
set -euo pipefail

MARKER="# Managed by WslRemoteSetup"
DROPIN="/etc/ssh/sshd_config.d/10-wsl-remote-setup.conf"
STATE_DIR="/var/lib/wsl-remote-setup"
STATE="$STATE_DIR/ssh.json"

die() { echo "[FAIL] $*" >&2; exit 1; }
step() { echo; echo "== $*"; }

USER_NAME=""; INSTALL_ID=""
while [ $# -gt 0 ]; do
  case "$1" in
    --user) USER_NAME="${2:-}"; shift 2 ;;
    --installation-id) INSTALL_ID="${2:-}"; shift 2 ;;
    *) die "알 수 없는 인수: $1" ;;
  esac
done

[ "$(id -u)" -eq 0 ] || die "sudo로 실행해야 합니다."
[[ "$USER_NAME" =~ ^[a-z_][a-z0-9_-]{0,31}$ ]] || die "사용자 이름 형식 오류"
[ "$USER_NAME" != "root" ] || die "root는 허용 사용자로 지정할 수 없습니다."
id "$USER_NAME" >/dev/null 2>&1 || die "Linux 사용자 $USER_NAME 없음"
[[ "$INSTALL_ID" =~ ^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$ ]] || die "installation-id 형식 오류"

step "1. openssh-server"
PREINSTALLED=false
if dpkg-query -W -f='${Status}' openssh-server 2>/dev/null | grep -q "install ok installed"; then
  PREINSTALLED=true
  echo "이미 설치됨: $(dpkg-query -W -f='${Version}' openssh-server)"
else
  export DEBIAN_FRONTEND=noninteractive
  apt-get update
  apt-get install -y --no-install-recommends openssh-server
fi
VERSION="$(dpkg-query -W -f='${Version}' openssh-server)"
grep -qE '^[[:space:]]*Include[[:space:]]+/etc/ssh/sshd_config\.d/\*\.conf' /etc/ssh/sshd_config \
  || die "sshd_config에 sshd_config.d Include가 없습니다. 설정을 변경하지 않았습니다."

step "2. drop-in $DROPIN"
BACKUP=""
if [ -e "$DROPIN" ]; then
  head -n 1 "$DROPIN" | grep -qF "$MARKER" || die "같은 이름의 다른 파일이 있습니다. 덮어쓰지 않습니다."
  BACKUP="$(mktemp)"; cp -p "$DROPIN" "$BACKUP"
fi
restore() {
  if [ -n "$BACKUP" ]; then cp -p "$BACKUP" "$DROPIN"; else rm -f "$DROPIN"; fi
}
TMP="$(mktemp "$DROPIN.XXXXXX")"
cat > "$TMP" <<EOF
$MARKER (installationId $INSTALL_ID). 직접 수정하지 마세요.
# sshd는 처음 읽은 값을 사용하므로 다른 drop-in보다 앞선 이름(10-)을 쓴다. 실제 유효값은 sshd -T로 확인한다.
PermitRootLogin no
AllowUsers $USER_NAME
PubkeyAuthentication yes
# 공개키 인증을 외부 클라이언트에서 확인하기 전까지 유일한 로그인 수단을 막지 않는다(FR-04).
PasswordAuthentication yes
KbdInteractiveAuthentication no
PermitEmptyPasswords no
MaxAuthTries 4
LoginGraceTime 30
X11Forwarding no
EOF
chmod 0644 "$TMP"
mv -f "$TMP" "$DROPIN"

step "3. 구문 검사 (sshd -t)"
install -d -m 0755 /run/sshd
if ! sshd -t; then restore; die "sshd -t 실패. drop-in을 원복했습니다."; fi
echo "OK"

step "4. 유효값 검증 (sshd -T)"
EFFECTIVE="$(sshd -T -C "user=$USER_NAME,host=lan-client,addr=192.0.2.10")"
check() { echo "$EFFECTIVE" | grep -qx "$1" || { restore; die "유효값 불일치: '$1' 기대. drop-in을 원복했습니다."; }; echo "  $1"; }
check "permitrootlogin no"
check "allowusers $USER_NAME"
check "pubkeyauthentication yes"
check "passwordauthentication yes"
check "permitemptypasswords no"
check "port 22"
[ -n "$BACKUP" ] && rm -f "$BACKUP"

step "5. 서비스"
if systemctl cat ssh.socket >/dev/null 2>&1; then UNIT=ssh.socket; else UNIT=ssh.service; fi
systemctl enable "$UNIT"
# 설정 반영: 소켓 활성화는 연결마다 sshd를 띄우므로 재시작이 필요 없고, 서비스 방식은 restart한다.
if [ "$UNIT" = ssh.service ]; then systemctl restart ssh.service; else systemctl start ssh.socket; fi
sleep 1
ss -ltnH | awk '$4 ~ /:22$/ {found=1; print "  listen " $4} END {exit !found}' || die "TCP 22 리스너가 없습니다."
echo "  $UNIT enabled=$(systemctl is-enabled "$UNIT") active=$(systemctl is-active "$UNIT")"

step "6. host key 지문(공개 정보)"
FPS=""
for k in /etc/ssh/ssh_host_*_key.pub; do
  fp="$(ssh-keygen -lf "$k")"; echo "  $fp"
  FPS="$FPS\"$(echo "$fp" | awk '{print $2 " " $NF}')\","
done

install -d -m 0755 "$STATE_DIR"
cat > "$STATE.tmp" <<EOF
{
  "schemaVersion": 1,
  "installationId": "$INSTALL_ID",
  "opensshServerPreinstalled": $PREINSTALLED,
  "opensshServerVersion": "$VERSION",
  "ownedFiles": ["$DROPIN"],
  "unit": "$UNIT",
  "allowUsers": ["$USER_NAME"],
  "passwordAuthentication": true,
  "hostKeyFingerprints": [${FPS%,}],
  "updatedAt": "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
}
EOF
chmod 0644 "$STATE.tmp"; mv -f "$STATE.tmp" "$STATE"
echo
echo "[PASS] OpenSSH 서버 구성 완료. 상태: $STATE"
echo "외부 인증·포워딩은 아직 구성되지 않았습니다(NOT_TESTED)."
