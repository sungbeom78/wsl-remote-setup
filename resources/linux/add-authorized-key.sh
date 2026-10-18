#!/bin/bash
# WslRemoteSetup — 공개키 1개를 대상 사용자 authorized_keys에 추가 (FR-04, T07)
#
# 사용: (대상 사용자로, sudo 불필요) bash add-authorized-key.sh --installation-id <guid>  < public_key_line
#
# - 표준입력의 한 줄만 받는다. 개인키·옵션(command= 등) 포함 줄·허용되지 않은 키 형식은 거부한다.
# - 기존 authorized_keys는 보존하고, 같은 키(형식+본문)가 이미 있으면 추가하지 않는다.
# - 도구가 추가한 키의 지문을 ~/.local/share/wsl-remote-setup/added-keys 에 기록한다(제거 시 그 키만 다루기 위함).
set -euo pipefail
die() { echo "[FAIL] $*" >&2; exit 1; }

INSTALL_ID=""
while [ $# -gt 0 ]; do
  case "$1" in
    --installation-id) INSTALL_ID="${2:-}"; shift 2 ;;
    *) die "알 수 없는 인수: $1" ;;
  esac
done
[[ "$INSTALL_ID" =~ ^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$ ]] || die "installation-id 형식 오류"
[ "$(id -u)" -ne 0 ] || die "root가 아니라 대상 사용자로 실행하세요."

IFS= read -r LINE || true
if IFS= read -r EXTRA && [ -n "$EXTRA" ]; then die "공개키는 한 줄만 받습니다."; fi
LINE="${LINE%$'\r'}"
LINE="${LINE#$'\xEF\xBB\xBF'}"   # Windows PowerShell 5.1 파이프가 붙이는 UTF-8 BOM 제거
[ -n "$LINE" ] || die "입력이 비어 있습니다."
[ "${#LINE}" -le 8192 ] || die "입력이 너무 깁니다."
case "$LINE" in *PRIVATE\ KEY*) die "개인키로 보입니다. 개인키는 받지 않습니다." ;; esac

read -r KTYPE KBODY KCOMMENT <<< "$LINE" || true
case "$KTYPE" in
  ssh-ed25519|ecdsa-sha2-nistp256|ecdsa-sha2-nistp384|ecdsa-sha2-nistp521|ssh-rsa|sk-ssh-ed25519@openssh.com|sk-ecdsa-sha2-nistp256@openssh.com) ;;
  *) die "허용되지 않은 키 형식 또는 옵션이 붙은 줄입니다: ${KTYPE:0:40}" ;;
esac
[[ "$KBODY" =~ ^[A-Za-z0-9+/]+={0,3}$ ]] || die "키 본문 형식 오류"

TMPKEY="$(mktemp)"; trap 'rm -f "$TMPKEY"' EXIT
printf '%s %s %s\n' "$KTYPE" "$KBODY" "${KCOMMENT:-}" > "$TMPKEY"
FP_LINE="$(ssh-keygen -lf "$TMPKEY")" || die "ssh-keygen이 키를 해석하지 못했습니다."
BITS="$(awk '{print $1}' <<< "$FP_LINE")"; FP="$(awk '{print $2}' <<< "$FP_LINE")"
if [ "$KTYPE" = ssh-rsa ] && [ "$BITS" -lt 3072 ]; then die "RSA 키는 3072비트 이상이어야 합니다($BITS)."; fi

install -d -m 0700 "$HOME/.ssh"
AK="$HOME/.ssh/authorized_keys"
touch "$AK"; chmod 0600 "$AK"
if awk -v t="$KTYPE" -v b="$KBODY" '{ for (i=1;i<NF;i++) if ($i==t && $(i+1)==b) found=1 } END { exit !found }' "$AK"; then
  echo "[PASS] 이미 등록된 키입니다(추가하지 않음): $FP"
  exit 0
fi

BEFORE="$(grep -cvE '^\s*(#|$)' "$AK" || true)"
printf '%s %s %s\n' "$KTYPE" "$KBODY" "${KCOMMENT:-}" >> "$AK"
AFTER="$(grep -cvE '^\s*(#|$)' "$AK" || true)"

REC_DIR="$HOME/.local/share/wsl-remote-setup"; install -d -m 0700 "$REC_DIR"
printf '%s %s %s %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$INSTALL_ID" "$KTYPE" "$FP" >> "$REC_DIR/added-keys"
chmod 0600 "$REC_DIR/added-keys"

echo "[PASS] 공개키 추가: $KTYPE $FP (${KCOMMENT:-주석 없음})"
echo "  authorized_keys 유효 줄: $BEFORE → $AFTER"
