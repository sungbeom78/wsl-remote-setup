#!/bin/bash
# WslRemoteSetup — Linux 기본 사용자 준비 (root로 실행: wsl -d <distro> -u root)
#
# 사용: bash init-user.sh --user <linux_user>
# - 사용자가 없으면 만들고 sudo 그룹에 넣는다(비밀번호는 이 스크립트가 다루지 않음 — passwd를 따로 실행).
# - /etc/wsl.conf의 [user] default를 이 사용자로 설정한다. 다른 섹션(예: [boot] systemd=true)은 보존한다.
#   변경은 배포판이 다음에 시작될 때 적용된다(배포판을 강제로 끄지 않음).
# - 이미 맞게 되어 있으면 아무것도 바꾸지 않는다.
set -euo pipefail
die() { echo "[FAIL] $*" >&2; exit 1; }

USER_NAME=""
while [ $# -gt 0 ]; do
  case "$1" in
    --user) USER_NAME="${2:-}"; shift 2 ;;
    *) die "알 수 없는 인수: $1" ;;
  esac
done
[ "$(id -u)" -eq 0 ] || die "root로 실행해야 합니다."
[[ "$USER_NAME" =~ ^[a-z_][a-z0-9_-]{0,31}$ ]] || die "사용자 이름 형식 오류"
[ "$USER_NAME" != root ] || die "root는 사용할 수 없습니다."

if id "$USER_NAME" >/dev/null 2>&1; then
  echo "USER_EXISTS"
else
  useradd --create-home --shell /bin/bash --groups sudo "$USER_NAME"
  echo "USER_CREATED"
fi
id -nG "$USER_NAME" | tr ' ' '\n' | grep -qx sudo || { usermod -aG sudo "$USER_NAME"; echo "SUDO_ADDED"; }

CONF=/etc/wsl.conf
touch "$CONF"
if awk -v u="$USER_NAME" '
  /^\[/ { sec=$0 }
  sec=="[user]" && $0 ~ "^[[:space:]]*default[[:space:]]*=" { split($0,a,"="); gsub(/[[:space:]]/,"",a[2]); if (a[2]==u) found=1 }
  END { exit !found }' "$CONF"; then
  echo "DEFAULT_OK"
else
  TMP="$(mktemp)"
  awk -v u="$USER_NAME" '
    BEGIN { insec=0; done=0 }
    /^\[/ {
      if (insec && !done) { print "default=" u; done=1 }
      insec = ($0 == "[user]")
      print; next
    }
    insec && $0 ~ "^[[:space:]]*default[[:space:]]*=" { if (!done) { print "default=" u; done=1 }; next }
    { print }
    END {
      if (insec && !done) { print "default=" u; done=1 }
      if (!done) { print ""; print "[user]"; print "default=" u }
    }' "$CONF" > "$TMP"
  cat "$TMP" > "$CONF"; rm -f "$TMP"
  echo "DEFAULT_SET"
fi

# 비밀번호가 설정됐는지(빈/잠긴 비밀번호면 사용자가 passwd로 설정해야 함)
case "$(passwd -S "$USER_NAME" | awk '{print $2}')" in
  P) echo "PASSWORD_SET" ;;
  *) echo "PASSWORD_MISSING" ;;
esac
