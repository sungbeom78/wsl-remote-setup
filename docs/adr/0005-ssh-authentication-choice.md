# ADR 0005 — SSH 로그인 방식 선택(비밀번호+공개키 / 공개키만)

- 상태: 채택 (사용자 결정, 2026-10-18)

## 맥락

G2에서 공개키 등록과 외부 공개키 로그인 확인까지 마쳤다(T07, T21 공개키 PASS). 그러나 사용자가 쓰는 mRemoteNG(내부적으로 PuTTY 사용)에서는 키를 쓰기 어렵다. 그래서 사용자는 G2의 비밀번호 로그인을 **유지**하기로 했다. 대신 제품에서는 두 방식 중 하나를 고를 수 있어야 한다.

## 결정

- `DesiredConfiguration.SshAuth`
  - `PasswordAndPublicKey`(기본): 비밀번호와 공개키 로그인을 모두 허용한다.
  - `PublicKeyOnly`: 공개키 로그인만 허용한다.
  - CLI 옵션: `--ssh-auth password|key-only`.
- 두 방식 모두 root 로그인 금지, 허용 사용자 지정, LAN 한정 방화벽, host key 검증은 똑같이 적용한다.
- 공개키만 선택했을 때는 계획에 `ssh-key-only` 단계가 `ssh-server` 단계 뒤에 추가된다. 이 단계는 다음 조건에서만 적용한다.
  - 공개키를 등록했다.
  - 외부 클라이언트에서 공개키 로그인을 확인했다.
  - 사용자가 승인한 뒤 직접 sudo로 실행한다.
  - 이 순서는 FR-04의 "유일한 로그인 수단을 먼저 없애지 않는다"를 지키기 위한 것이다.
- `install-openssh.sh --password-auth yes|no`
  - `no`는 대상 사용자의 `authorized_keys`에 유효한 키가 있을 때만 허용한다.
  - 설정 후 `sshd -T`로 실제 적용된 값이 선택한 방식과 같은지 확인한다.
- 이미 설치된 환경에서 방식을 바꿀 때는 `disable-password-auth.sh`를 쓴다. 이 스크립트는 확인된 키 지문이 있어야 실행된다.

## G2 적용 상태

- 비밀번호+공개키(기본)를 유지한다. `disable-password-auth.sh`는 실행하지 않았다.
- 등록된 공개키 `laptop-1`(ED25519 `SHA256:rLsuV2GO…`)는 그대로 둔다.

## 제한

- 비밀번호 로그인이 LAN(192.168.1.0/24)에 열려 있다. sshd에 보이는 출발지 주소는 portproxy 때문에 항상 172.24.208.1이다. 그래서 출발지 범위는 Windows 방화벽 규칙으로만 제한된다.
- 비밀번호 대입 공격 대응(fail2ban 등)은 1.0 범위가 아니다. `MaxAuthTries 4`와 `LoginGraceTime 30`만 적용되어 있다.
