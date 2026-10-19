# 테스트 결과

## 2026-10-17 — M0 읽기 전용 진단

- 환경: G2 (SERVERPC), Windows 11 Pro 23H2 22631.6199
- 실행 주체: ownerAdmin, RDP 세션, 비승격
- 조건: 읽기 전용 조회만 수행
- 로그/원본: `.local/G2_ENVIRONMENT.json`, `.local/G2_ENVIRONMENT_REPORT.md`

| 항목 | 결과 | 비고 |
|---|---|---|
| OS·에디션·빌드 조회 | PASS | |
| 계정·그룹·세션 조회 | PASS | |
| 네트워크 설정 조회 | PASS | 수동 IPv4 확인 |
| RDP 설정·리스너 조회 | PASS | 포트 7389 |
| 방화벽 규칙 조회 | PASS / WARN | 기본 3389 Any 규칙 활성 |
| WSL 기능 상태 조회 | PASS | CIM 사용, DISM은 NOT_TESTED(승격 필요) |
| 다른 계정(consoleuser) WSL 조회 | NOT_TESTED | |
| 코드 빌드·단위 테스트 | NOT_TESTED | 코드 없음 |
| 실제 SSH/RDP 인증 | NOT_TESTED | |

## 2026-10-17 — B3 WSL 런타임 설치 (시스템 변경, 사용자 승인)

- 실행 주체: ownerAdmin, UAC 승격 PowerShell 5.1
- 명령: `wsl.exe --install --no-distribution` (inbox wsl.exe 10.0.22621.1)
- 로그: `.local/logs/B3_wsl_install_20261017_214622.log`

| 항목 | 결과 | 비고 |
|---|---|---|
| 명령 종료코드 | FAIL | exit 1, 첫 줄 "액세스가 거부되었습니다." |
| VirtualMachinePlatform | PASS (재부팅 대기) | InstallState 1 |
| Microsoft-Windows-Subsystem-Linux | FAIL | InstallState 2 |
| Store WSL 패키지 | FAIL | `C:\Program Files\WSL` 없음 |
| 승격 DISM 원인 조회 | NOT_TESTED | UAC 취소 |

## 2026-10-18 — B3 재시도 후 검증 (재부팅 1회 후)

- 실행: 사용자가 관리자 PowerShell에서 `wsl --install --no-distribution` 직접 실행. 검증은 ownerAdmin 비승격.

| 항목 | 결과 | 비고 |
|---|---|---|
| `wsl --version` | PASS | WSL 3.0.1.0, kernel 6.18.40.1-1 |
| `wsl --status` 기본 버전 2 | PASS | WSL1 미지원(선택 구성요소 Disabled) — NOT_APPLICABLE |
| VMP·하이퍼바이저 | PASS | HypervisorPresent=True |
| 재부팅 대기 플래그 | PASS | 없음 |
| `Ubuntu-24.04` 온라인 식별자 | PASS | |
| ownerAdmin 배포판 | NOT_APPLICABLE | 아직 미등록(B4) |
| IP·RDP 포트 보존 | PASS | 192.168.1.222/24 Manual, 7389 |

## 2026-10-18 — B4 Ubuntu-24.04 등록 (시스템 변경, 사용자 승인)

- 실행 주체: ownerAdmin 비승격, 명령 `wsl --install -d Ubuntu-24.04 --no-launch`

| 항목 | 결과 | 비고 |
|---|---|---|
| 설치 종료코드 | PASS | exit 0, 약 54초 |
| 배포판 등록·WSL 2 | PASS | `Ubuntu-24.04 Stopped 2` |
| 소유자 = ownerAdmin | PASS | HKCU Lxss에 등록 |
| Linux 기본 사용자 | PENDING → PASS | 사용자 초기화 후 DefaultUid 1000 (`linux_admin`) |

### B4 초기화 후 검증 (ownerAdmin 비승격, `wsl -d Ubuntu-24.04 -e /bin/bash <고정 스크립트>`)

| 항목 | 결과 | 비고 |
|---|---|---|
| 기본 사용자 linux_admin | PASS | uid 1000, sudo 그룹 |
| home | PASS | /home/linux_admin, 750 |
| OS 버전 | PASS | Ubuntu 24.04.5 LTS |
| PID 1 systemd | PASS | wsl.conf systemd=true |
| systemd 상태 | WARN | degraded: systemd-binfmt.service |
| sudo 비밀번호 인증 | NOT_TESTED | sudo -n은 비밀번호 요구(정상) |
| DNS·HTTPS | PASS | archive.ubuntu.com 200 |
| WSL NAT 주소 | PASS | eth0 172.24.220.111/20, lo 10.255.255.254 제외 필요 |
| openssh-server | NOT_APPLICABLE | 미설치(M4 대상) |

## 2026-10-18 — B5 개발 도구 (사용자 승인)

| 항목 | 결과 | 비고 |
|---|---|---|
| Git.Git (winget) | PASS | 2.55.0.windows.5 |
| Microsoft.DotNet.SDK.10 (winget) | PASS | SDK 10.0.402, NETCore/AspNetCore/WindowsDesktop 10.0.13 |

## 2026-10-18 — M2 빌드·단위 테스트

- 환경: G2, .NET SDK 10.0.402, ownerAdmin 비승격
- 명령: `dotnet build WslRemoteSetup.slnx -c Debug` / `dotnet test WslRemoteSetup.slnx --no-build`
- 로그: `.local/logs/test/unit.trx`

| 항목 | 결과 | 비고 |
|---|---|---|
| 빌드(경고를 오류로 처리) | PASS | 경고 0 |
| 단위 테스트 | PASS | 88/88. 검증·CIDR·WSL 주소·디코딩·마스킹·allowlist·Doctor·Planner·StateStore |
| 관련 시나리오 | PASS(단위) | T02, T04, T17(계획 해시), T18(손상 비초기화), T20, T25, T26 |

## 2026-10-18 — M2 G2 실기 (읽기 전용, 시스템 변경 없음)

- 실행: `src\WslRemoteSetup\bin\Debug\net10.0-windows\win-x64\WslRemoteSetup.exe`, ownerAdmin 비승격 RDP 세션
- 로그: `.local/logs/doctor_g2.json`

| 항목 | 결과 | 비고 |
|---|---|---|
| doctor | PASS | exit 0, 8초. 실측값과 일치(RDP 7389, 192.168.1.222/24 수동, Private, WSL 3.0.3.0, Ubuntu-24.04 uid 1000) |
| doctor 실행 후 배포판 상태 | PASS | `Stopped` 유지(기동하지 않음) |
| 방화벽 평가 1차 | FAIL → 수정 | 앱 범위 와일드카드 규칙을 RDP 허용으로 오집계. 수정 후 `RDP change port[Private/LocalSubnet]` PASS |
| plan | PASS | 1~4·8단계 변경 없음, 5단계 확인 필요, 6·7·9단계 변경 필요(M4/M5) |
| status | PASS | 상태 파일 없음을 정상 보고, 폴더를 만들지 않음 |
| 비대화형 메뉴 | PASS | exit 3 UserInputRequired |
| 잘못된 입력(`--linux-user root`) | PASS | exit 2, 거부 |
| 실제 SSH/RDP 인증 | NOT_TESTED | |

## 2026-10-18 — M3 코드 단계 (시스템 변경 없음)

- 환경: G2, .NET SDK 10.0.402, ownerAdmin 비승격
- 로그: `.local/logs/test/*.trx`

| 항목 | 결과 | 비고 |
|---|---|---|
| 단위 테스트 | PASS | 131/131. 요청 변조 12종, 정책, 검증기(T10 논리), 조정기(T11·T17), 코디네이터 |
| 통합: 같은 사용자 파이프 왕복·호출자 SID | PASS | 메시지가 아니라 파이프 가장으로 SID 확인 |
| 통합: 변조 요청 | PASS | 서버에서 Denied |
| 통합: 초과 요청 | FAIL → 수정 → PASS | 1차 실행은 교착으로 10초 timeout. 버린 뒤 응답하도록 수정 |
| 통합: ACL에 없는 사용자 연결 | PASS | UnauthorizedAccessException |
| 통합 반복 3회 | PASS | 흔들림 없음 |
| T09/T10 실계정(다른 로컬 사용자) | NOT_TESTED | 일반 테스트 계정이 필요하며 생성은 승인 대상 |
| SYSTEM 서비스 컨텍스트 | NOT_TESTED | 서비스 등록은 승인 대상 |

## 2026-10-18 — M3 실기 POC: WSL 수명 (사용자 승인: 배포판 기동 허용)

- 환경: G2, WSL 3.0.3.0, Ubuntu-24.04(systemd 사용), `.wslconfig` 없음
- 실행 주체: ownerAdmin 비승격 RDP 세션
- 명령: `WslRemoteSetup.exe poc-lifetime --hold 120`
- 로그: `.local/logs/poc_lifetime_g2.json`

| 항목 | 결과 | 실제값 |
|---|---|---|
| A. 일회성 명령 뒤 유휴 종료 | PASS | `/bin/true` exit 0. 16초 뒤 Stopped. systemd만으로는 인스턴스가 유지되지 않음 |
| B. keeper 보유 중 유지 | PASS | 120초 동안 표본 12개 모두 Running, keeper PID 유지 |
| B. 보유 중 주소 안정 | PASS | 172.24.220.111 → 172.24.220.111 (A 단계와도 같음) |
| C. keeper 해제 뒤 유휴 종료 | PASS | 16초 뒤 Stopped, 남은 wsl.exe 없음, 재기동 없음 |
| terminate/shutdown 호출 | 해당 없음 | 호출하지 않음 |
| T14 RDP 끊기·잠금 상태 유지 | NOT_TESTED | |
| T15/T16 로그아웃·재부팅·pause 후 shutdown | NOT_TESTED | 별도 승인 대상 |

## 2026-10-18 — M4 WSL OpenSSH 서버 (사용자 승인, 사용자가 직접 sudo 실행)

- 명령: 사용자 일반 PowerShell에서 `wsl -d Ubuntu-24.04 -- sudo bash …/resources/linux/install-openssh.sh --user linux_admin --installation-id 5f3c9a10-…`
  - sudo 비밀번호는 사용자가 직접 입력했다(1회 재입력).
- 사후 검증: `resources/linux/verify-ssh.sh`(읽기 전용)

| 항목 | 결과 | 실제값 |
|---|---|---|
| 패키지 설치 | PASS | openssh-server 1:9.6p1-3ubuntu13.20 (도구가 설치함, 기존 설치 없음). openssh-client 13.19 → 13.20 동반 업그레이드 |
| drop-in 소유·내용 | PASS | `10-wsl-remote-setup.conf` 하나뿐, 마커 있음 |
| `sshd -t` | PASS | |
| `sshd -T` 유효값 | PASS | permitrootlogin no, allowusers linux_admin, pubkey yes, password yes(임시), permitemptypasswords no, port 22 |
| 서비스·리스너 | PASS | ssh.socket enabled/active, 0.0.0.0:22, [::]:22 |
| SSH 배너 (WSL 내부 127.0.0.1:22) | PASS | `SSH-2.0-OpenSSH_9.6p1 Ubuntu-3ubuntu13.20` |
| host key 지문 | 기록 | ED25519 `SHA256:qjDdYL7ZZ9YgGsiu6CQ9etjth6qZm4SDvHVUPU3MsR8`, ECDSA `SHA256:8K7Y/…`, RSA `SHA256:ETKw…` |
| Windows localhost:22 | 관찰 | WSL localhost 포워딩으로 127.0.0.1/::1:22만 수신하고 배너 응답. LAN 노출은 없음 |
| 실제 SSH 인증(비밀번호/공개키) | NOT_TESTED | |
| LAN 경로 | NOT_TESTED | 포워딩·방화벽 미구성 |

## 2026-10-18 — M4 forward plan (읽기 전용, 비승격)

| 항목 | 결과 | 비고 |
|---|---|---|
| 단위/통합 테스트 | PASS | 159 / 5 |
| `forward apply` 비승격 실행 | PASS | `ELEVATION_REQUIRED`로 거부, exit 2, 변경 없음 |
| `ssh-keyscan.exe`(Windows OpenSSH 9.5p1) | FAIL → 대체 | `choose_kex: unsupported KEX method sntrup761x25519-sha512`. `ssh.exe -v`(KEX·host key 알고리즘 지정, 인증 none)로 지문을 읽도록 바꿈 |
| 1차 plan | 올바르게 차단 | host key를 읽지 못해 적용 불가로 판정 |
| 관찰 | 기록 | 클라이언트가 없으면 배포판이 약 16초 뒤 종료되어 sshd도 종료됨(journal: signal 15). 그 동안 연결이 거부됨. LAN 접속에는 keeper(M5)가 필요함 |
| 2차 plan(배포판 유지 중) | PASS (아래 apply로 이어짐) | 192.168.1.222:1004 → 172.24.220.111:22, ED25519 지문 일치, 방화벽 Private·192.168.1.0/24, 차단 사유 없음 |

## 2026-10-18 — M4 forward apply (시스템 변경, 사용자 승인)

- 실행 주체: ownerAdmin, UAC 승격 PowerShell 5.1 창. 사용자가 계획을 확인하고 `Y`를 입력했다.
- 로그: `.local/logs/M4_forward_apply_*.log`
- 과정
  - 1차 시도: 한글 확인어 `적용`이 CP949/UTF-8 불일치로 깨져 취소됨(exit 3, 변경 없음). ADR 0004에 따라 `[Y/n]`으로 수정했다.
  - UAC 취소 2회: 변경 없음을 매번 확인했다.
- 배포판 유지: 적용하는 동안 일반 세션에서 keeper(`--exec /bin/sleep`)를 보유했다.

| 항목 | 결과 | 실제값 |
|---|---|---|
| portproxy | PASS | `192.168.1.222:1004 → 172.24.220.111:22` (다른 tuple 없음) |
| 방화벽 규칙 | PASS | `WslRemoteSetup SSH 1004 (5f3c9a10)` Inbound Allow TCP 1004, Local 192.168.1.222, Remote 192.168.1.0/255.255.255.0, Private, Group WslRemoteSetup |
| 수신 | PASS | 192.168.1.222:1004 (iphlpsvc) |
| 경유 SSH 배너(G2 자신) | PASS | `SSH-2.0-OpenSSH_9.6p1 Ubuntu-3ubuntu13.20` |
| 경유 host key | PASS | ED25519 `SHA256:qjDdYL7ZZ9YgGsiu6CQ9etjth6qZm4SDvHVUPU3MsR8` |
| 보존 확인 | PASS | 192.168.1.222/24 Manual, GW 192.168.1.1, DNS 8.8.8.8, RDP 7389 리스너 유지 |
| 상태 폴더 ACL | PASS | SYSTEM·Administrators FullControl, Users ReadAndExecute. 일반 사용자 쓰기 거부 확인 |
| 상태 파일 | PASS (결함 1) | 소유 리소스 2건, before 스냅샷, 승인 기록, Phase AwaitingAuthenticationSetup. OwnerSid·DistroName 누락 → 코드 수정(다음 apply부터 반영) |
| T20 G2 자체 TCP 검사 | PASS | 외부 인증은 아래 항목으로 따로 기록 |
| T21 다른 LAN PC 실제 SSH 인증 | NOT_TESTED | |
| T11 WSL 재시작 후 주소 변경 | PENDING | 자동 갱신은 M5. 그 전에는 `forward apply`를 다시 실행해야 함 |

## 2026-10-18 10:48 — T21 다른 LAN PC 실제 SSH 인증 (사용자 수행)

- 클라이언트: 같은 LAN(192.168.1.0/24)에 있는 사용자 PC. G2가 아님.
- 명령: `ssh -p 1004 linux_admin@192.168.1.222`

| 항목 | 결과 | 실제값 |
|---|---|---|
| 경로 | PASS | 클라이언트 → 192.168.1.222:1004 → portproxy → 172.24.220.111:22 |
| host key 확인 | PASS | 첫 접속에서 표시된 ED25519 지문 `SHA256:qjDdYL7ZZ9YgGsiu6CQ9etjth6qZm4SDvHVUPU3MsR8`이 등록값과 같아 사용자가 수락함 |
| 인증 | PASS (비밀번호) | `linux_admin` 비밀번호 로그인 성공. Ubuntu 24.04.5 셸 진입 |
| 공개키 인증 | NOT_TESTED | 아직 키를 등록하지 않음 |
| 관찰 | 기록 | 커널 6.18.54.1-microsoft-standard-WSL2(이전 6.18.40.1 — WSL 자동 업데이트). eth0 주소는 172.24.220.111로 변화 없음 |
| 확인할 것 | PENDING | SSH 세션만 있고 wsl.exe 클라이언트(keeper)가 없을 때 배포판이 유지되는지. M5 수명 설계에 반영한다 |

## 2026-10-18 13:45 — 공개키 등록 (사용자 승인·사용자 제공 공개키)

- 키: 클라이언트 `laptop-1`, ED25519 `SHA256:rLsuV2GOHCsNqErpHrCv3cIM+P+WdqPj8PrhG9W5Zko`
  - 사용자가 공개키 한 줄만 제공했다. 개인키는 받지 않았다.
- 명령: `add-authorized-key.sh --installation-id 5f3c9a10-…`. linux_admin으로 실행했고 sudo는 쓰지 않았다.

| 항목 | 결과 | 실제값 |
|---|---|---|
| 거부 테스트(사전) | PASS | 개인키 표지, `command=` 옵션 줄, ssh-dss, 본문 손상 키를 모두 거부함 |
| PS 5.1 파이프 BOM | 결함 → 수정 | stdin 앞에 BOM이 붙어 키 형식 판별이 실패하던 문제. 스크립트에서 BOM을 제거하도록 고침 |
| 등록 | PASS | authorized_keys 유효 줄 0 → 1. 권한은 `~/.ssh` 700, `authorized_keys` 600 |
| 중복 방지(T07) | PASS | 같은 키로 다시 실행하면 "이미 등록됨"으로 끝나고 추가하지 않음 |
| 추가 기록 | PASS | `~/.local/share/wsl-remote-setup/added-keys`에 지문과 설치 ID를 남김 |
| 포워딩 유지 | PASS | WSL eth0 172.24.220.111로 변화 없음. 1004 경유 배너 정상 |
| 외부 공개키 로그인 | NOT_TESTED | 사용자 확인 대기 |

## 2026-10-18 14:08 — 외부 공개키 로그인 (사용자 수행)

- 클라이언트: `laptop-1`. 명령: `ssh -o PreferredAuthentications=publickey -o PasswordAuthentication=no G2`
- 첫 시도는 클라이언트 config에 `Port 7389`(RDP 포트)를 적어 `Connection reset`으로 실패했다. `Port 1004`로 고친 뒤 성공했다. 서버는 바꾸지 않았다.

| 항목 | 결과 | 실제값 |
|---|---|---|
| 공개키만으로 로그인 | PASS | 비밀번호 질문 없이 셸 진입 |
| 서버 로그 | PASS | `Accepted publickey for linux_admin from 172.24.208.1 … ED25519 SHA256:rLsuV2GOHCsNqErpHrCv3cIM+P+WdqPj8PrhG9W5Zko` |
| 관찰: 접속 출발지 주소 | 기록 | portproxy를 거치면 sshd에 보이는 출발지가 항상 172.24.208.1(WSL 호스트)이다. 출발지 IP 제한은 sshd가 아니라 Windows 방화벽(192.168.1.0/24)에서 한다 |
| 관찰: host key 확인 부작용 | 기록 | 지문 확인용 접속이 sshd 로그에 `Invalid user wrs-hostkey-probe`로 남는다. 의도된 동작이며 인증은 시도하지 않는다 |

## 2026-10-18 — SSH 인증 방식 결정 (사용자)

| 항목 | 결과 | 비고 |
|---|---|---|
| G2 비밀번호 로그인 해제 | NOT_APPLICABLE | 사용자 결정에 따라 유지(mRemoteNG 사용). `disable-password-auth.sh`는 실행하지 않음 |
| 제품 선택 옵션 `--ssh-auth password|key-only` | PASS(단위·CLI) | 단위 172 / 통합 5. key-only를 고르면 `ssh-key-only` 단계가 추가되고, 잘못된 값은 exit 2 |
| `install-openssh.sh --password-auth` | 구문 PASS | 실기 재실행은 하지 않음(G2는 이미 yes로 구성됨) |

## 2026-10-18 — M5 코드 단계 (시스템 변경 없음)

| 항목 | 결과 | 비고 |
|---|---|---|
| 단위 테스트 | PASS | 188. 에이전트 루프 9개(T11 주소 변경, T13 backoff·상한·경고 1회, T16 pause 중 재기동 없음, keeper 크래시 반복 억제, 재확인 주기). 로그온 작업 XML 5개 |
| 통합 테스트 | PASS | 9. 보호 경로 ACL 판정: 사용자 temp·저장소 빌드 산출물은 신뢰하지 않음, System32는 신뢰 |
| `conhost.exe --headless` | PASS | 22631에서 창 없이 실행되고 exit 0 |
| 단일 EXE publish | PASS | self-contained win-x64 73.2 MB, SHA-256 `9a2d6f0b…6a10299`, 버전 `0.1.0-m2+7a3997e` |
| `autostart plan` | PASS | 적용 가능. 시스템 변경 없음 |
| 서비스·로그온 작업 실기 | PENDING | 승인 대기 |

## 2026-10-18 — M5 autostart install 실기 (사용자 승인) 및 결함

- 실행: 관리자 창. 사용자가 계획을 확인하고 `Y`를 입력했다. 로그: `.local/logs/M5_autostart_install_*.log`

| 항목 | 결과 | 실제값 |
|---|---|---|
| 설치 5단계 | PASS | 실행 파일, 정책, 서비스, 로그온 작업, /Run 모두 완료. exit 0 |
| 서비스 | PASS | WslRemoteSetupNet Running, Auto, LocalSystem, `"C:\Program Files\WslRemoteSetup\WslRemoteSetup.exe" service` |
| 로그온 작업 | PASS | Running, Interactive only, Run As ownerAdmin |
| 설치 파일 ACL | PASS | SYSTEM·Administrators Full, Users·AppPackages 읽기만 |
| 에이전트 keeper | PASS | Ubuntu-24.04 Running, 1004 경유 배너 정상 |
| 보존 확인 | PASS | 192.168.1.222/24 Manual, RDP 7389 |
| **서비스의 호출자 확인** | **FAIL → 수정** | 서비스 로그 `caller=`(빈 값) → 모든 요청 Denied(안전 실패, 포워딩은 변경되지 않음). 원인: 파이프에서 데이터를 읽기 전에 가장을 시도함. 또 .NET `GetCurrent`는 SYSTEM에서 Identification 토큰을 열지 못함. 수정: 읽은 뒤 `ImpersonateNamedPipeClient` → `OpenThreadToken(OpenAsSelf=TRUE)` → `RevertToSelf` |
| 결함 예방 | 코드 반영 | 실행 파일을 교체하기 전에 작업 종료와 남은 에이전트 정리. keeper를 Job 개체(KILL_ON_JOB_CLOSE)에 묶음 |
| 화면·기록 개선 | 코드 반영 | ADR 0006. 단위 190 / 통합 9 PASS. 새 publish SHA-256 `6fbca2c8…630c1ee` |
| 수정본 재설치 | PENDING | 승인 대기 |

## 2026-10-18 15:17 — 수정본 재설치 (사용자 승인)

- 실행: 관리자 창 `autostart install`(인수 없음. 설치 ID와 서버 키는 기록에서 읽음). 사용자가 `Y`를 입력했다.
- 기록: `C:\ProgramData\WslRemoteSetup\logs\history.log`

| 항목 | 결과 | 실제값 |
|---|---|---|
| 쉬운 화면 문구 | PASS | 설치 ID·SID·SHA-256·키 지문이 화면에는 없고, 기록 파일에만 단계별로 남음 |
| 실행 파일 교체 | PASS | 서비스 중지 → 작업 종료 → 남은 에이전트(PID 15700) 정리 → 교체. 설치본 SHA-256 `6fbca2c8…630c1ee` |
| 서비스 호출자 확인(수정 확인) | PASS | 재시작 직후 에이전트 요청에 `NoChange`(이전에는 `Denied`). 에이전트 상태: lastConfirmed 172.24.220.111, 연속 실패 0 |
| 고아 keeper | PASS | wsl.exe keeper는 새 에이전트의 자식 하나뿐(PID 15216 → 13432). 이전 keeper는 남지 않음 |
| 연결 | PASS | portproxy 유지, 1004 경유 배너 정상 |
| 기록 단계 번호 | 관찰 | 기록은 [1] 상태 확인, [2]~[6] 설치. 화면은 [1/5]~[5/5]로 번호가 한 칸 다름(사소함) |
| pause/resume 실기 | PENDING | WSL이 종료되어 열린 SSH 세션이 끊길 수 있어 별도 확인 필요 |
| T15 로그아웃·재부팅 후 자동 복구 | NOT_TESTED | 별도 승인 대상 |

## 2026-10-18 15:24~15:29 — pause/resume 실기 (사용자 승인: SSH 일시 단절 허용)

| 항목 | 결과 | 실제값 |
|---|---|---|
| T16 `pause` | PASS | 38초 뒤 keeper 해제, 54초 뒤 WSL Stopped. 이후 120초 동안 재기동 없음. 상태 `paused: true, keeper: false` |
| pause 중 접속 | 기대대로 | 1004 TCP는 수락되지만 SSH 응답 없음(WSL 꺼짐) |
| `resume-monitoring` | PASS | 31초 뒤 keeper 재시작, 관리부 응답 `NoChange`, 1004 경유 배너 정상 |
| T11 실제 주소 변경 | NOT_TESTED | 재기동 후에도 WSL 주소가 172.24.220.111로 같았다(NAT 대역 유지). 주소 변경 자동 갱신은 단위 테스트로만 확인 |
| 화면 문구 | 수정 | pause/resume 안내의 "keeper" 표현을 쉬운 말로 바꿈(다음 설치 때 반영) |

## 2026-10-18 — T15 재부팅 시험 준비 (사용자 승인)

- 재부팅 전 기준값: LastBoot 2026-10-17 21:57:55, 서비스 Running/Auto, 로그온 작업 Running, portproxy 192.168.1.222:1004 → 172.24.220.111:22, 에이전트 paused=false·keeper=true·NoChange
- 세션: ownerAdmin RDP 1개. 다른 사용자 세션은 없음
- 기대 결과
  - 부팅 직후, 로그인 전: 서비스는 Running이고 WSL은 꺼져 있다. 1004로 SSH 응답이 없다(1.0 한계, NOT_APPLICABLE).
  - ownerAdmin이 RDP로 로그인하고 약 15초 뒤: 로그온 작업이 에이전트를 시작하고, keeper가 WSL을 켜고, 포워딩 확인 결과가 나오고, 1004로 SSH 응답이 온다.

## 2026-10-18 15:46~16:05 — T15 재부팅 후 자동 복구 (사용자 승인)

- 재부팅: `shutdown /r /t 60`, LastBoot 2026-10-18 15:46:15. 사용자가 15:57에 ownerAdmin으로 RDP 재접속.

| 항목 | 결과 | 실제값 |
|---|---|---|
| 로그인 전 서비스 자동 시작 | PASS | 15:48:34 네트워크 관리부 시작 |
| 로그인 전 WSL·SSH | NOT_APPLICABLE | 1.0 한계(로그인 후 동작) |
| 로그인 후 에이전트 자동 시작 | PASS | 15:57:23 작업 실행 → 15:57:24 에이전트·keeper 시작(keeper는 에이전트 자식 하나뿐) |
| 포워딩 확인 응답 | PASS (판단 오류 포함) | 15:57:35 `NoChange`(서비스의 호출자 확인 정상) |
| WSL 주소 | 변화 없음 | 172.24.220.111 (T11 실기는 여전히 NOT_TESTED) |
| 보존 | PASS | 192.168.1.222/24 Manual, RDP 7389 |
| **1004 수신** | **FAIL** | portproxy 설정은 있지만 수신 프로세스가 없어 `연결 거부`. Wi-Fi 연결은 15:46:28. 부팅 직후 IP Helper가 192.168.1.222가 준비되기 전에 바인딩에 실패한 뒤 다시 시도하지 않은 것으로 판단(원인 추정, 이벤트 근거 없음) |
| 결함 원인(제품) | 확인 | 서비스·에이전트가 "규칙 설정 존재"만 보고 "실제 수신"은 확인하지 않아 `NoChange`로 판단 |
| 수정(코드) | PASS(단위) | 서비스가 시작 10초 뒤·주소 변경 시·60초마다 소유 규칙의 실제 수신을 확인하고, 끊겼으면 같은 규칙을 지웠다 다시 추가. 에이전트 요청 때도 같은 확인. 기록과 다른 규칙이나 남의 규칙은 건드리지 않음. 단위 193 / 통합 9. publish SHA-256 `4fc75f0e…bef7bb2` |
| 수정본 G2 적용 | PENDING | 재설치 승인 대기. 현재 G2의 1004 SSH는 끊긴 상태 |

## 2026-10-18 16:16 — 수신 자가 복구 수정본 재설치 (사용자 승인)

| 항목 | 결과 | 실제값 |
|---|---|---|
| 재설치 | PASS | 설치본 SHA-256 `4fc75f0e…bef7bb2`, 5단계 완료, exit 0 |
| 수신 자가 복구 | PASS | 서비스 로그 `caller=S-1-5-21-…-1002 … Updated: 수신이 끊겨 있어 같은 규칙으로 다시 적용`, 1004 수신 192.168.1.222:1004 (IP Helper) |
| 1004 경유 SSH | PASS | `SSH-2.0-OpenSSH_9.6p1 Ubuntu-3ubuntu13.20` |
| keeper | PASS | 에이전트 → wsl.exe → wsl.exe 체인 하나 |
| 재부팅 직후 타이머 경로 복구 | NOT_TESTED | 이번 복구는 에이전트 요청 경로였다. 서비스 시작 10초 뒤·주소 변경 시 점검 경로는 다음 재부팅에서 확인한다 |

## 2026-10-18 — T15 재시험 준비 (사용자 승인)

- 목적: 재부팅 직후 로그인하기 **전에** 서비스가 타이머(시작 10초 뒤·주소 변경 시·60초마다)로 1004 수신을 스스로 복구하는지 확인한다.
- 재접속 후 확인할 것:
  - 서비스 로그에 `수신이 끊겨 있어 같은 규칙으로 다시 적용했습니다`가 있는지, 그 시각이 로그인(RDP)보다 앞서는지
  - 1004 수신 여부와 SSH 배너
  - 에이전트가 `NoChange`로 응답받는지

## 2026-10-18 16:27~16:35 — T15 재시험: 재부팅 직후 자가 복구 (사용자 승인)

| 항목 | 결과 | 실제값 |
|---|---|---|
| 부팅·Wi-Fi | 기록 | LastBoot 16:27:13, Wi-Fi 연결 16:27:26 |
| 서비스 자동 시작 | PASS | 16:29:38 |
| **로그인 전 수신 자가 복구(타이머 경로)** | **PASS** | 16:29:49 `192.168.1.222:1004 수신이 끊겨 있어 같은 규칙으로 다시 적용했습니다.` → 같은 원인(부팅 직후 바인딩 실패)이 다시 발생했고, 시작 10초 뒤 점검에서 복구됨 |
| 로그인 후 에이전트 | PASS | RDP 로그인 16:34 → 에이전트·keeper 16:34:55 → 16:35:21 `NoChange` |
| 1004 경유 SSH | PASS | 수신 192.168.1.222:1004, 배너 `SSH-2.0-OpenSSH_9.6p1 Ubuntu-3ubuntu13.20` |
| 보존 | PASS | 192.168.1.222/24 Manual, RDP 7389 |
| T15 종합 | PASS | 로그인 후 SSH 사용 가능. 로그인 전 SSH는 1.0 범위 밖(NOT_APPLICABLE) |

## 2026-10-18 — status 화면 개선 (시스템 변경 없음)

| 항목 | 결과 | 비고 |
|---|---|---|
| 단위 테스트 | PASS | 201 (StatusEvaluator 8개: 정상, 멈춤, 수신 끊김(T15 상황), 에이전트 미실행, 다른 계정, 오래된 점검·실패, 서비스 중지·규칙 없음 안내, 미설치) |
| 통합 테스트 | PASS | 9 |
| G2 실행(개발 빌드, 비승격) | PASS | 종합 "모두 정상입니다…" 그리고 7개 항목(SSH 접속, WSL, 자동 연결 관리, 백그라운드 프로그램, 로그인 시 자동 실행, 연결 통로, 방화벽) 모두 정상. 화면에 SID·설치 ID 없음. 기술 값은 사용자 작업 기록에만 남음. WSL을 켜지 않음(실행 중 목록만 조회) |
| 설치본 반영 | PENDING | `C:\Program Files`의 설치본은 이전 버전. 다음 `autostart install` 때 반영 |

## 2026-10-18 — 설치 질문·포트 추천·DHCP 경고 (시스템 변경 없음)

| 항목 | 결과 | 비고 |
|---|---|---|
| 단위 테스트 | PASS | 227. 포트 판정(RDP 7389·Windows 예약 범위·사용 중·다른 연결 전달 규칙·자기 규칙 재사용)과 다음 빈 포트 추천(T04), 질문 입력 해석(Enter = 기본값), DHCP·주소 변경 상태 표시 |
| `netsh … excludedportrange` 해석 | PASS | G2 실측 출력(5357, 50000-50059, 58742-59141)을 현지화 문구와 무관하게 읽음 |
| status "이 PC 주소" | PASS(G2) | `192.168.1.222 (고정 주소)` |
| 대화형 질문 실기 | NOT_TESTED | 콘솔 입력이 필요해 자동 실행으로는 확인 불가. 다음 사용자 시연 때 확인 |

## 2026-10-18 — DHCP 주소 따라가기 (ADR 0007, 시스템 변경 없음)

| 항목 | 결과 | 비고 |
|---|---|---|
| 판단 로직 단위 테스트 | PASS | 9개. 그대로 유지, 같은 카드·같은 LAN 범위에서 이동, 꺼짐, 다른 LAN, 공용 네트워크(T12), 다른 카드 사용 금지(FR-05), 후보 여러 개, link-local, 정책 검증(/16보다 넓은 범위 거부) |
| 서비스 연동 | 빌드 PASS | 감시 주기마다 판단 → Move면 소유 규칙만 이동(이전 규칙이 기록과 다르면 중단), 방화벽 로컬 주소만 변경, 정책·상태 갱신, 작업 기록 남김 |
| `autostart install` 질문 | 빌드 PASS | DHCP일 때만 "주소가 바뀌면 따라갈까요? [Y/n]" |
| 실기 | NOT_TESTED | G2는 고정 주소. DHCP를 쓰는 PC(M7)에서 확인 필요 |

## 2026-10-18 — 통합 설치 마법사 `install`/`resume` (코드 단계, 시스템 변경 없음)

| 항목 | 결과 | 비고 |
|---|---|---|
| 8단계 흐름 | 빌드 PASS | 질문 → WSL 설치(UAC, 재부팅 필요 시 저장·안내·[y/N]) → 소유자 계정 확인 → 배포판 설치 → Linux 사용자(`wsl -u root`, 비밀번호는 같은 화면에서 직접) → SSH(+공개키 선택) → 연결 통로(UAC) → 자동 실행(UAC) → status |
| 이어하기 | 설계 | 단계마다 실제 상태를 다시 확인해 끝난 단계는 건너뜀. 진행 위치는 사용자 상태에 기록 |
| `init-user.sh` wsl.conf 병합 | PASS | 복사본 4종(기존·systemd만·다른 섹션 포함·빈 파일)에서 [user] default 설정. 다른 섹션 보존, 재실행해도 바뀌지 않음 |
| 내장 스크립트 | PASS | 통합 테스트: 6개 포함·LF 추출, Windows→/mnt 경로 변환 |
| 확인 입력 [y/N] | PASS | 재부팅·선택 항목은 Enter = 아니요(단위 테스트) |
| 문의 채널 | PASS | 도움말·마법사 끝·중단 화면에 `문의·의견: https://www.edubeom.com/` |
| 단위 / 통합 | PASS | 242 / 17 |
| 실기(사용자 시연) | NOT_TESTED | G2는 이미 설치되어 있어 대부분 "이미 됨"으로 건너뜀. 신규 PC(M7)에서 전 과정 확인 필요 |

## 2026-10-18 — 단계별 되돌리기 `uninstall`·문의 채널 (코드 단계, 시스템 변경 없음)

| 항목 | 결과 | 비고 |
|---|---|---|
| 5단계 흐름 | 빌드 PASS | 1) 외부 연결 닫기(UAC, `forward remove`) 2) 자동 실행 중지(UAC, `autostart remove`) 3) WSL 안 SSH 설정 되돌리기(root, 패키지는 남김) 4) (선택) 배포판 삭제([y/N] + 이름 직접 입력) 5) 설치 이력·직접 지우는 방법. 단계마다 영향 고지·건너뛰기 가능 |
| 설치 프로그램 보존 | 코드 반영 | `autostart remove`가 실행 파일은 지우지 않고 위치만 안내(사용자 결정) |
| `revert-ssh.sh` 키 제거 | PASS(복사본) | 임시 폴더: 이 도구가 등록한 키 1개만 제거, 다른 키·주석 보존, 기록에서 해당 설치 ID 줄만 제거 |
| 대화형 메뉴 | 코드 반영 | 1 설치/이어서, 2 상태, 3 연결 복구(설치 재확인), 4 설정 변경(질문 다시), 5 잠시 멈춤/다시 켜기, 6 되돌리기 |
| 문의 채널 | PASS | 도움말·메뉴·마법사 끝·중단 화면·status 하단에 `문의·의견: https://www.edubeom.com/` |
| 단위 / 통합 | PASS | 242 / 18 |
| 실기 | NOT_TESTED | G2의 실제 연결·설정을 지우는 작업이라 실행하지 않음. 사용자 승인 후 실행하거나 신규 PC(M7)에서 확인 |

## 2026-10-18 — 여러 서비스 포트 (후속 범위, 시스템 변경 없음)

| 항목 | 결과 | 비고 |
|---|---|---|
| 조정기 | PASS(단위) | SSH와 함께 추가, WSL 주소 변경 시 같이 이동(T11), 빠진 서비스 규칙 보충, 남의 규칙은 건너뛰되 SSH는 막지 않음, 수신 자가 복구 |
| 검증 | PASS(단위) | 이름 형식, SSH 포트와 겹침, 외부 포트 중복, 예약 이름 'ssh', 최대 10개 |
| 명령 | 빌드 PASS | `forward apply --service web:8080:80`(반복 가능), `forward add-service`, `forward remove-service --name web`. 자동 실행이 있으면 보호 정책을 고치고 백그라운드 프로그램을 다시 시작 |
| 설치 질문 | 빌드 PASS | "SSH 말고 더 열 포트가 있나요? [y/N]" → 이름·WSL 포트·외부 포트(추천값) |
| status | PASS(단위·G2 회귀) | "추가 서비스 web — 열려 있음 (외부 8080번 → WSL 80번…)" |
| 되돌리기 | 설계 | `forward remove`/`uninstall` 1단계가 소유 연결 통로·방화벽 규칙 전부를 대상으로 하므로 서비스 포트도 함께 닫힘 |
| 단위 / 통합 | PASS | 247 / 25 |
| 실기 | NOT_TESTED | G2에 추가 포트를 열지 않음(승인 필요) |

## 2026-10-18 — 이 PC 주소 고정 마법사 (ADR 0008, 후속 범위)

| 항목 | 결과 | 비고 |
|---|---|---|
| 계획 단위 테스트 | PASS | 13개. 지금 주소 고정(끊김 없음), 다른 주소(끊김·5분 원복 경고), 범위 밖·첫/마지막 주소·게이트웨이·형식 오류·사용 중 차단, 이미 고정(G2), 게이트웨이 없음, DNS 대체, 마스크 계산 |
| G2 `network plan` | PASS | "이미 고정 주소입니다"로 진행 불가, 변경 없음 |
| 비승격 `network apply` | PASS | 관리자 권한 필요 안내, exit 2 |
| 설치·되돌리기 연동 | 빌드 PASS | 설치: DHCP일 때만 [y/N] 제안. 되돌리기: 이 도구가 고정한 경우에만 [y/N] 해제 |
| 실기 적용·자동 원복 | NOT_TESTED | DHCP를 쓰는 PC가 필요(M7) |
| 단위 / 통합 | PASS | 260 / 25 |

## 2026-10-18 — 로그인 전 무인 실행 POC 준비 (코드 단계, 시스템 변경 없음)

| 항목 | 결과 | 비고 |
|---|---|---|
| 부팅 작업 XML | PASS(단위) | BootTrigger(+30초), 소유자 SID, LogonType **S4U**(암호 저장 안 함), LeastPrivilege, 비밀번호 요소 없음 |
| 에이전트 중복 방지 | 코드 반영 | 세션별 Local 뮤텍스를 사용자 프로필 잠금 파일로 바꿈. 부팅 세션과 로그인 세션에서 에이전트가 하나만 돌게 함. 로그에 실행 세션 번호를 남김 |
| POC 명령 | 빌드 PASS | `autostart boot-poc-create` / `boot-poc-remove`(관리자, [Y/n]). scheduled-task로 기록해 되돌리기 2단계에도 포함 |
| publish | PASS | SHA-256 `825a198d…c9c752c` |
| 실기 POC | PENDING | 승인 대기: 수정본 재설치 → POC 작업 등록 → 재부팅 → **로그인하지 않은 상태에서** 다른 PC로 SSH 시험 |

## 2026-10-18 17:55 — 무인 실행 POC 준비 실기 (사용자 승인)

| 항목 | 결과 | 비고 |
|---|---|---|
| 수정본 재설치 | PASS | 설치본 SHA-256 `825a198d…`, 5단계 완료 |
| POC 부팅 작업 등록 | PASS | `\WslRemoteSetup\BootAgent-5f3c9a10` Ready/Enabled, Run As ownerAdmin, Logon Mode **Interactive/Background(S4U)** |
| 새 에이전트(잠금 파일) | PASS | 세션 2에서 시작, NoChange |
| 재부팅 시험 | PENDING | 재부팅 후 **로그인 전에** 노트북에서 `ssh G2` → 이후 RDP 로그인 → 로그로 세션 0(부팅 작업) 에이전트 동작 여부 확인 |

## 2026-10-18 17:58~18:09 — 무인 실행 POC 1차 재부팅 시험

- 사용자: 재부팅 후 로그인 전 노트북에서 `ssh G2` → "리눅스 접속 실패". 18:09 RDP 로그인.

| 항목 | 결과 | 실제값 |
|---|---|---|
| 부팅 작업(S4U) 실행 | **PASS** | 17:59:31 에이전트 시작 "실행 세션 0 (비대화형)". 비밀번호 저장 없음 |
| 로그인 전 WSL·sshd | **PASS** | WSL 부팅 17:59:11, ssh.socket 17:59:35 Listening. 에이전트가 세션 0에서 `wsl` 실행·주소 확인 성공 |
| 백그라운드 연결 관리 프로그램 | 늦음 | **지연된 자동 시작** → 18:01:09 시작(부팅 후 약 2분 20초) |
| 1004 통로 | 늦게 복구 | 18:01:20 수신 복구(부팅 직후 IP Helper 바인딩 실패 — T15와 같은 원인). 그 전까지 1004 연결 거부 |
| 에이전트 확인 | PASS | 18:01:39 NoChange(서비스가 아직 없던 17:59:53, 18:00:38에는 연결 실패 후 backoff) |
| sshd 접속 기록 | 없음 | 부팅 이후 외부 접속이 sshd까지 오지 않음 → Windows 쪽(통로 닫힘)에서 막힌 것 |
| 판정 | **부분 PASS** | 로그인 전 무인 실행 자체는 동작함. 외부 접속은 부팅 후 약 2분 30초까지 불가. 시험 시각이 그 이전이었던 것으로 판단(사용자 시각 미확인) |
| 개선(코드) | 반영 | 서비스 시작 유형을 지연 없는 자동 시작으로(`sc create/config start= auto`). 재설치 후 2차 시험 필요 |

## 2026-10-18 18:21 — POC 2차 준비 (사용자 승인)

| 항목 | 결과 | 실제값 |
|---|---|---|
| 수정본 재설치 | PASS | 설치본 SHA-256 `33845bc4…` = publish 산출물, 5단계 완료 |
| 서비스 시작 유형 | PASS | StartMode=Auto, DelayedAutoStart=False |
| 부팅 작업 | PASS | Ready(재설치 때 세션 0 에이전트가 정리되어 다음 부팅에 다시 실행) |
| 2차 재부팅 시험 | PENDING | 로그인 전 1분·3분에 사용자가 `ssh G2` 시험 |
- 2026-10-18 18:38: 사용자 요청으로 예약 재부팅을 취소함(`shutdown /a` exit 0, LastBoot 17:58:51 그대로). 2차 재부팅 시험은 보류.
- 2026-10-18 18:4x: 사용자 결정 — 사람이 SSH로 시험하지 않는 **자동 측정** 방식으로 2차 재부팅 시험. 판정 근거: 부팅 시각, 에이전트 세션 0 시작, WSL ssh.socket Listening, 서비스 시작, 1004 수신 복구 기록. 외부 PC 실제 로그인은 NOT_TESTED로 남김.

## 2026-10-18 18:42~18:50 — 무인 실행 POC 2차 재부팅 (기록 기반 판정)

| 시각 | 부팅 후 | 기록 |
|---|---|---|
| 18:42:43 | 0초 | LastBoot |
| 18:42:55 | +12초 | 서비스 시작(지연 없는 자동 시작) |
| 18:42:56 | +13초 | Wi-Fi 연결 |
| 18:42:58 | +15초 | 1004 수신 복구(주소 변경 알림으로 즉시 점검, 같은 초에 3회 기록) |
| 18:43:23 | +40초 | 부팅 작업 에이전트 "실행 세션 0 (비대화형)", keeper 시작 |
| 18:43:30 | +47초 | 포워딩 확인 NoChange |
| 18:50 | +7분 | RDP 로그인 |

- 판정: **로그인 전 무인 실행 PASS(기록 기준)** — S4U(암호 저장 없음)·자동 로그인 없이 부팅 후 약 47초에 SSH 경로 준비 완료.
- 외부 PC 실제 로그인(로그인 전): **NOT_TESTED** — 사용자 결정으로 수동 시험 생략. 다음 재부팅 때 로그인 전에 `ssh G2` 한 번으로 확정 가능.
- 관찰: 주소 변경 알림이 짧은 시간에 여러 번 와서 같은 초에 재적용이 3회 일어남(동작에는 문제없음). 후속 개선 후보: 알림 묶기(debounce).

### 정정 (2026-10-18 19:0x)

- 위 "로그인 전 무인 실행 PASS(기록 기준)" 판정은 **철회**한다. 사용자가 로그인 전에 노트북에서 여러 번 접속했지만 실패했다. 이번 부팅 이후 sshd 접속 기록이 0건이다.
- 판정 오류 원인: 에이전트의 "포워딩 확인 NoChange"는 규칙 설정 값만 비교한 결과이고 실제 SSH 응답은 확인하지 않는다. 그래서 경로 준비 근거가 될 수 없다.
- 판정: 로그인 전 무인 실행 — 에이전트·WSL·sshd 기동은 PASS, **외부 SSH 경로는 FAIL**(원인 미확정).
- 마지막 1회 시도(사용자 허용): 실제 경로 측정 기록 추가
  - 서비스: `192.168.1.222:1004` 경유 SSH 배너. 상태가 바뀔 때만 기록하고, 응답이 없으면 15초 간격으로 확인
  - 에이전트: WSL `IP:22` 직접 배너

## 2026-10-18 20:23~20:38 — 무인 실행 마지막 시도(측정 기록) · 종료

| 시각 | 부팅 후 | 기록 |
|---|---|---|
| 20:23:55 | 0초 | LastBoot |
| 20:24:07 | +12초 | 서비스 시작 |
| 20:24:08 / 20:24:12 | +13초 / +17초 | Wi-Fi 연결. 네트워크 프로필이 식별 중 Public → Private |
| 20:24:10 | +15초 | 1004 수신 복구. 경유 SSH 응답 없음(WSL 아직 꺼짐) |
| 20:24:35 | +40초 | 부팅 작업 에이전트(세션 0) 시작 |
| 20:24:39 | +44초 | WSL sshd 시작 |
| **20:24:40** | **+45초** | **192.168.1.222:1004 경유 SSH 응답 정상**(G2 내부에서 확인) |
| 20:24:42 | +47초 | WSL SSH 직접 응답 정상(세션 0) |
| 20:38 | +14분 | RDP 로그인 |

- 사용자: 로그인 전 노트북에서 접속 실패.
- sshd 기록(로그인 전 32줄): 측정용 연결(약 1분 간격, 출발지 172.24.208.1)뿐이다. 노트북 시도는 sshd에 도달하지 않았다.
- 결론
  - 로그인 전 무인 실행에서 **G2 내부 경로는 PASS**(부팅 후 45초).
  - **외부 LAN → G2 수신은 FAIL**. 로그인 전 외부 inbound가 Windows 쪽에서 막힌 것으로 판단한다(같은 PC 내부 연결은 방화벽을 거치지 않음). 원인은 미확정이다.
- 사용자 결정으로 무인 실행 POC는 여기서 **종료**한다. 1.0 지원 범위는 "로그인 후"로 유지한다(개발 계획대로).
- 다시 시도할 경우의 첫 단계(기록용): 방화벽 드롭 로그(`netsh advfirewall set allprofiles logging droppedconnections enable`, 승인 필요)를 켠 상태로 1회 재부팅해 차단 여부와 적용 규칙을 확인한다.
- POC 부팅 작업 `\WslRemoteSetup\BootAgent-5f3c9a10`는 남아 있다. 로그인 후 동작에는 영향이 없다(에이전트 하나만 실행). 정리는 `autostart boot-poc-remove` 또는 `uninstall` 2단계로 한다.

## 2026-10-18 21:01 — ADR 0009 ① export-client (G2 실기, 읽기 전용)

- 환경: G2, ownerAdmin(비승격), Debug 빌드. 출력 위치는 Claude 작업용 임시 폴더(`--out`), 바탕화면은 건드리지 않음.
- 조건: 시스템 설정 변경 없음. WSL에서 `/etc/ssh/ssh_host_ed25519_key.pub` 읽기와 `id -un`만 실행.
- 결과: **PASS** — 이름 SERVERPC, 192.168.1.222:1004, linux_admin, RDP 7389/ownerAdmin. 서버 키 지문 `SHA256:qjDdY…MsR8`가 설치 상태 기록과 일치. 파일에 비밀번호·설치 ID·SID 없음.
- 로그: `%LOCALAPPDATA%\WslRemoteSetup\logs\history.log`
- `client` 모드: 코드·단위 테스트(연결 정보 검증, config/known_hosts 편집·충돌, 공개키 등록 명령 정화)만 완료. 노트북 실기는 **NOT_TESTED**.

## 2026-10-18 21:2x — ADR 0009 ③ ARM64 빌드 · ④ 절전 점검

- **ARM64 게시 PASS**
  - 명령: `dotnet publish src/WslRemoteSetup -c Release -r win-arm64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -p:DebugType=embedded -o .\artifacts\publish\win-arm64`
  - 결과: `WslRemoteSetup.exe` 단일 파일(약 82 MB). PE 머신 형식 `AA64`(ARM64) 확인.
  - ARM 장비에서의 실행은 **NOT_TESTED**. doctor는 ARM64 서버를 WARN으로 표시한다.
- **절전 점검(G2, 읽기 전용) PASS**
  - 전원 API로 읽은 값: AC 절전은 0(사용 안 함), AC 최대 절전은 10800초. `powercfg /q`의 0x2a30과 일치한다.
  - `status`와 `power`가 "전원에 연결되어 있어도 3시간 동안 쓰지 않으면 잠듭니다"를 [확인 필요]로 표시한다.
  - `power keep-awake` / `power restore`는 코드와 [Y/n]만 준비했다. G2 적용은 승인 대기(**NOT_TESTED**).
- **관찰(별건)**
  - `status`의 "자동 연결 관리: 실행되고 있지 않습니다"는 다음 상황 때문이다.
    - POC 부팅 작업(BootAgent, S4U, 세션 0)의 에이전트가 계속 실행 중이다.
    - 이 에이전트가 단일 실행 잠금을 쥐고 있다.
    - 그래서 20:39 로그온 작업 에이전트는 바로 종료되었다.
  - SSH 접속 자체는 정상이다.
  - 해결: `autostart boot-poc-remove`로 POC 작업을 제거한다(관리자 창, 승인 필요). 그 뒤 로그온 작업을 다시 실행하거나 다시 로그인한다.

## 2026-10-18 21:31~21:36 — 절전 안 함 적용 · POC 부팅 작업 제거 · 남은 에이전트 정리 (사용자 승인)

- 환경: G2, 관리자 창(UAC는 사용자가 승인, Y는 사용자가 입력), `artifacts\publish\win-x64` 최신 빌드.
- **`autostart boot-poc-remove` PASS**: `\WslRemoteSetup\BootAgent-5f3c9a10` 삭제를 확인했다.
- **`power keep-awake` PASS**
  - AC 절전 0, AC 최대 절전 10800 → 0(적용 후 다시 읽어 일치).
  - DC 값 10800은 바꾸지 않았다.
  - 이전 값은 `%LOCALAPPDATA%\WslRemoteSetup\user-state\power-before.json`에 저장했다.
- **결함 발견 → 수정**
  - 증상: 작업 `/End`·삭제 후에도 그 작업이 띄운 에이전트(세션 0, S4U)가 남아 단일 실행 잠금을 쥐었다. 그래서 로그인 자동 실행 에이전트가 시작되지 못했다.
  - 일반 권한에서는 종료가 거부됐다("Access is denied").
  - 조치: 관리자 창에서 PID와 명령줄(` agent `)을 확인한 뒤 종료하고, 로그인 작업을 다시 실행했다(사용자 승인).
  - 21:35:40 세션 2에서 에이전트와 keeper가 시작됐다. 21:35:43 포워딩 NoChange, WSL SSH 직접 응답 정상.
  - `status`: "모두 정상"(절전 포함).
  - 코드 수정 1: `boot-poc-remove`가 세션 0의 우리 에이전트를 끝내고 로그인 작업을 다시 시작한다.
  - 코드 수정 2: `status`는 프로세스를 볼 수 없을 때 최근 3분 안의 점검 기록으로 실행 여부를 판단한다.
  - 두 수정은 단위·통합 테스트만 통과했고, 설치본에 반영하지 않아 실기에서는 확인하지 않았다(**NOT_TESTED**).

## 2026-10-18 21:56~22:00 — 로그인 전 외부 SSH 재시험 (사용자 승인) · **PASS**

- **배경:** 21:40쯤 사용자의 mRemoteNG "G2-linux" 주소가 `192.168.1.221`로 잘못 설정된 것을 발견했다(G2는 `.222`). 주소를 고친 뒤 접속에 성공했다.
  - 이전 로그인 전 시험의 "외부 LAN 실패"(17:58, 18:42, 20:23 항목)는 이 잘못된 주소로 시험한 결과였을 가능성이 높다.
  - 따라서 그 결론은 **무효로 정정한다**(원인 확정은 아니지만 재시험 결과와 모순됨).
- **조건**
  - `autostart boot-poc-create`로 시험 작업(BootTrigger, S4U)을 다시 등록하고 재부팅했다.
  - 사용자는 로그인하지 않은 상태에서 노트북 mRemoteNG(`192.168.1.222:1004`, 비밀번호)로 접속했다.
- **타임라인**

  | 시각 | 이벤트 |
  |---|---|
  | 21:56:59 | 부팅 |
  | 21:57:39 | 세션 0 에이전트 시작 |
  | 21:57:46 | WSL SSH 직접 응답 정상(+47초) |
  | **21:59:50** | sshd `Accepted password for linux_admin` (+2분 51초) |
  | 22:00:05 | 사용자 로그온(RDP) |

- **판정 근거**
  - 이 도구의 점검(배너 읽기)은 인증하지 않는다. 따라서 로그온 이전의 `Accepted password`는 외부 기기의 실제 로그인이다.
  - 사용자가 노트북에서 접속 성공을 직접 확인했다.
- **결과:** **PASS**. 로그인 전 무인 실행(부팅 작업 + 서비스)으로 외부 LAN SSH 접속이 가능하다. 1분 시점 시도 여부는 확인하지 않았다.
- **로그**
  - `%LOCALAPPDATA%\WslRemoteSetup\logs\agent.log`
  - WSL `journalctl -u ssh`
  - Security/TerminalServices 이벤트

## 2026-10-18 22:2x — 로그인 전 접속 선택 기능(ADR 0010) 코드 · G2 읽기 확인

- 코드: 설치 질문 6번(기본 아니오), `autostart install --prelogin`, `autostart prelogin-on|off`, status 표시, 되돌리기 정리.
- 테스트: 빌드 경고 0. 단위 297, 통합 25 통과.
- G2 `status`(Debug 빌드, 일반 권한, 읽기 전용): "로그인 전 접속: 켜짐" 표시 PASS. 세션 0 에이전트는 최근 점검 기록으로 "실행 중" 표시 PASS. 비승격 `prelogin-on`은 관리자 권한 안내 후 종료(exit 2).
- `prelogin-on/off` 적용과 설치 마법사 6번 흐름은 실기 **NOT_TESTED**. 설치본(Program Files)은 이전 빌드다.

## 2026-10-18 23:19 — Windows 업데이트 재부팅(25H2) 후 자동 복구 (관찰)

- 23:19:35 winlogon이 재시작을 시작했다(Windows Update). 23:19:50 부팅. 이후 빌드는 **25H2 26200.9457**(이전 23H2 22631). KB5129195·KB5124007·KB5126052가 설치됐다.
- 23:26:01 사용자가 RDP로 로그인했다. 23:27:13 세션 0(로그인 전 작업) 에이전트가 시작됐고, 23:27:50 WSL SSH 직접 응답이 정상이었다.
  - 업데이트 마무리로 부팅 후 약 7분 늦어진 것으로 보인다.
- 기능 업데이트 후에도 portproxy·방화벽·서비스·작업이 유지되어 자동 복구됐다. 로그인 전 접속을 이 재부팅에서 확인하지는 않았다(**NOT_TESTED**).

## 2026-10-19 00:26 — 최신 빌드 재설치 (사용자 승인) · PASS

- 관리자 창에서 `autostart install --prelogin on`을 실행했다(UAC·Y는 사용자가 입력). 설치본 버전은 `0.1.0-m2+a5a341d`이다.
- 교체 중 남은 에이전트(PID 14108)를 종료했다. 서비스·작업은 기존 등록을 유지했다. 로그인 전 작업은 다시 등록했다(켜기 완료).
- 00:26:14 세션 2에서 에이전트를 시작했다. 00:26:16 WSL SSH 직접 응답 정상.
- `status`: 모두 정상. 표시 항목은 다음과 같다.
  - 로그인 전 접속: 켜짐
  - 절전: 잠들지 않음
- SSH가 끊긴 시간은 수 초로 추정한다(외부 기기로 측정하지 않음).

## 2026-10-19 01:xx — 되돌리기 4단계: 삭제 전 백업 (코드)

- 사용자 제안 반영. `uninstall` 4단계에서 삭제를 선택하면 "삭제하기 전에 백업할까요? (권장) [Y/n]"을 묻는다.
- 위치를 입력받는다(Enter = `문서\WSL 백업\<배포판>-<날짜시각>.tar`, 폴더만 입력하면 기본 파일 이름).
- 사전 검사: 전체 경로, .tar, WSL 내부 경로 금지, 기존 파일 덮어쓰기 금지, 남은 공간 ≥ 사용 크기(`df`)×1.1.
- WSL을 끈 뒤 `wsl --export`로 저장한다. 파일이 확인되지 않으면 **삭제하지 않는다**.
- 삭제 후 복원 명령(`wsl --import …`)을 화면과 기록에 남긴다.
- 테스트: 단위 312, 통합 25 통과. 실제 export·삭제는 G2 데이터 보호를 위해 **NOT_TESTED**(M7 신규 PC에서 확인 예정).

## 2026-10-19 09:31~09:39 — 되돌리기 전 과정 실기 (사용자 직접 실행, 릴리스 0.1.0)

| 단계 | 결과 | 비고 |
|---|---|---|
| 1 외부 연결 닫기 | **FAIL(결함)** | 09:32:42에 portproxy와 방화벽 규칙을 지웠다. 그러나 서비스와 에이전트가 살아 있어 복구 요청으로 portproxy가 다시 생겼다. 방화벽 허용 규칙은 없어 외부에서는 들어올 수 없다. |
| 2 자동 실행 중지 | **부분(결함)** | 서비스, 로그온·로그인 전 작업, 정책 파일은 제거됐다. 그러나 이미 실행 중이던 로그온 에이전트(세션 2, 00:26 시작)가 남았다. |
| 3 SSH 설정 되돌리기 | PASS | 도구가 만든 설정 파일과 등록한 키 1개를 지웠고, SSH를 껐다. 패키지는 유지했다. |
| 4 Ubuntu 백업 → 삭제 | PASS | 사용 중 2.12GB, 백업 tar 1.71GB(`문서\WSL 백업\Ubuntu-24.04-20261019-0937.tar`). export 0, unregister 0. 복원 명령을 기록했다. |
| 절전 복원 | 건너뜀 | 사용자 선택(유지). |
| 보존 확인 | PASS | IP 192.168.1.222, RDP 7389 리스너, WSL 프로그램, 설치 EXE 유지. |

- 결함 수정(코드, 실기 재확인 전 NOT_TESTED):
  - `forward remove`는 규칙을 지우기 전에 서비스를 멈추고 정책 파일을 지운다(`DisableAutoRepair`).
  - `autostart remove`는 작업을 지운 뒤 모든 세션의 우리 에이전트를 끝낸다(자기 자신 제외).
- G2에 남은 것: portproxy 192.168.1.222:1004 → 172.24.220.111:22(대상 없음), 에이전트 프로세스 1개. 정리는 승인 후 진행한다.
