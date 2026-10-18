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
