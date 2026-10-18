# ADR 0003 — 소유자 에이전트 ↔ 네트워크 관리부 권한 분리와 IPC

- 상태: 제안 (M3 코드 단계 완료, 실기 POC 미수행)
- 날짜: 2026-10-18

## 맥락

WSL 배포판은 소유자 Windows 계정의 세션에서만 다룰 수 있다(ADR 0001). 반면 portproxy와 방화벽 갱신에는 관리자 권한이 필요하다.

허용하지 않는 방법:
- 소유자를 Administrators에 넣어 이 문제를 회피하는 것
- SYSTEM으로 `wsl.exe`를 호출하는 것

## 결정

| 구성요소 | 실행 주체 | 하는 일 | 하지 않는 일 |
|---|---|---|---|
| 소유자 에이전트 | 소유자 로그온 작업, 비승격 | WSL 수명 유지, 고정 스크립트로 WSL IPv4 관찰(`ip -j`), 갱신 요청 전송, pause 존중 | 포트·주소·명령 지정 |
| 네트워크 관리부 | 같은 EXE의 Windows 서비스 모드 | 관리자 정책과 직접 관찰한 사실로 요청 검증, 소유한 portproxy tuple 하나만 갱신 | WSL 실행, 셸·스크립트 실행, 정책 수정 |

### 정책 (관리자만 수정)

- 위치: `%ProgramData%\WslRemoteSetup\policy\forwarding.json`
- ACL: SYSTEM·Administrators만 쓰기 가능
- 필드:
  - 식별: `installationId`, `ownerSid`
  - 포워딩: `listenAddress`, `listenPort`, `connectPort`
  - 갱신 제한: `minUpdateInterval`
  - `sshHostKeyFingerprints`: M4에서 사용

### IPC

- 로컬 named pipe `WslRemoteSetup.Net.<installationId>`
- ACL:
  - 허용: SYSTEM·Administrators는 FullControl, 소유자 SID는 ReadWrite
  - 거부: NETWORK(S-1-5-2)
- 서버는 `RunAsClient`(가장)로 **호출자의 실제 SID**를 확인한다. 메시지 안의 SID는 신뢰하지 않는다.
- 요청은 1 KB 이하 JSON이다. 허용 필드는 정확히 `v`, `installationId`, `observedWslIPv4` 세 개이며, 알 수 없는 필드나 중복 필드가 있으면 거부한다.
- 응답은 결정 코드와 짧은 메시지만 담는다. 내부 경로나 명령은 노출하지 않는다.

### 검증 (관리부)

다음 조건을 모두 충족해야 허용한다.

1. 호출자 SID가 `ownerSid`와 같다.
2. `installationId`가 정책과 같다.
3. IPv4가 정규 형식이고 RFC1918 사설 주소이며, loopback·link-local이 아니다.
4. IPv4가 **관리부가 직접 조회한** WSL 호스트 가상 어댑터(vEthernet WSL)의 CIDR 안에 있다. 요청자가 보낸 대역은 신뢰하지 않는다.
5. 대상 `ip:connectPort`에 TCP 연결이 되고, M4에서는 SSH host key 지문이 등록값과 같다.
6. `minUpdateInterval` 안에서 반복 갱신하지 않는다(rate limit).

### portproxy 조정

- 수신 주소와 포트는 정책 값만 사용한다. 요청으로는 바꿀 수 없다.
- 기존 규칙에 따른 처리:

| 기존 규칙 | 처리 |
|---|---|
| 없음 | Add |
| 대상이 같음 | NoChange |
| 대상이 다르고, 상태에 기록된 이 도구의 이전 대상과 같음 | Update |
| 대상이 다르고, 기록된 이전 대상과도 다름(외부에서 변경됨) | Conflict로 정지 |

- 정책에 없는 다른 tuple은 읽기만 한다. 전체 reset은 하지 않는다.

## 대안

- **소유자 Administrators + 승격 작업**: CLAUDE.md 금지 사항이라 배제.
- **관리부가 WSL IP를 직접 조회(SYSTEM으로 wsl.exe 실행)**: 소유자 배포판이 보인다고 보장할 수 없어 배제.
- **portproxy 대신 mirrored 네트워킹**: 1.0 범위 밖. ADR로 후속 검토.
- **TCP loopback이나 파일 기반 IPC**: 호출자 인증이 약해 배제.

## WSL 수명 유지 실기 결과 (2026-10-18, G2)

- 실행: `WslRemoteSetup.exe poc-lifetime --hold 120`, 실행 주체 ownerAdmin 비승격. `.wslconfig` 없음(기본값).
- 결과
  - 일회성 명령이 끝나 클라이언트가 없어지면 **약 16초 뒤 배포판이 Stopped**가 된다. systemd가 켜져 있어도 마찬가지다.
  - keeper(`wsl.exe --distribution Ubuntu-24.04 --exec /bin/sleep infinity`)를 보유한 120초 동안 10초 간격 표본 12개가 모두 Running이었다.
  - eth0 주소는 172.24.220.111로 유지됐다.
  - keeper만 해제하자 16초 뒤 Stopped가 되었고, 남은 wsl.exe 클라이언트는 없었다. terminate나 shutdown은 호출하지 않았다.
- 결정
  - 소유자 에이전트는 foreground wsl.exe 클라이언트 하나를 보유하는 방식으로 수명을 관리한다.
  - pause는 "keeper 해제 + 재기동 금지"로 구현한다. 해제만으로 배포판이 자연 종료되므로 별도의 terminate는 필요 없다.
- 남은 확인
  - RDP 연결 끊기·잠금 상태에서 keeper가 유지되는지
  - 로그아웃 시 keeper 종료(1.0 한계로 표시)
  - 장시간 보유 시 메모리
  - 배포판이 중지된 뒤 짧은 시간 안에 재기동하면 주소가 유지됐다. VM 유휴 종료 이후의 주소 변경(T11)은 아직 관찰하지 못했다.

## 제한·미검증 (M3 실기 POC에서 확인할 것)

- 서비스 등록·실행은 승인 대상이다. 그 전까지 서비스 모드는 단위·통합 테스트(같은 사용자의 파이프)로만 검증한다.
- G2의 소유자 ownerAdmin은 관리자 그룹 구성원이므로, 일반 사용자 소유자 경계(T09/T10)는 별도 일반 테스트 계정이 있어야 입증된다.
- WSL 수명 유지 방식(foreground `wsl.exe` 프로세스 보유)은 실기 POC가 필요하다. 이 POC는 배포판을 기동하므로 착수 전에 알린다.
