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
