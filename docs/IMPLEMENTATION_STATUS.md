# 구현 현황

최종 갱신: 2026-10-18 (세션 1, M2 완료 시점)

## 단계 요약

| 단계 | 상태 | 비고 |
|---|---|---|
| M0 G2 진단·현황·변경 계획 | **완료** | `.local/G2_ENVIRONMENT_REPORT.md`, `.local/G2_ENVIRONMENT.json` |
| M1 WSL·Ubuntu·개발 도구 | **완료** | B3 WSL2 PASS, B4 Ubuntu-24.04/linux_admin PASS, B5 Git 2.55 + .NET SDK 10.0.402 PASS |
| M2 솔루션·doctor·plan·상태·단위 테스트 | **완료** | 빌드 경고 0, 단위 테스트 88/88 통과, G2 실기 doctor/plan/status 확인 |
| M3 권한 분리·WSL 수명 POC | 다음 | 아래 "다음 단계" 참고 |
| M4~M7 | 대기 | — |

## 보존 대상 (변경 금지)

다음 값과 설정은 변경하지 않는다.

- IP 192.168.1.222/24, GW 192.168.1.1, DNS 8.8.8.8, Wi-Fi 프로필 HOME-WIFI
- RDP 포트 7389·NLA, 방화벽 규칙 `RDP change port`
- ownerAdmin·consoleuser 계정과 그룹 구성

## 결정

- WSL 소유자: **ownerAdmin** (SID …-1002). 2026-10-17 사용자가 확정함 → `docs/adr/0001-g2-wsl-owner-account.md`
- 솔루션 구조·진단 방식 → `docs/adr/0002-solution-structure-and-probing.md`

## M2 결과 (2026-10-18)

- 구성
  - `WslRemoteSetup.slnx`, `global.json`(SDK 10.0.402, latestPatch), `Directory.Build.props`(nullable, 경고를 오류로 처리, 분석기 latest-recommended)
  - `src/WslRemoteSetup.Core` (net10.0)
    - 입력 검증
    - IPv4/CIDR 처리
    - WSL 주소 선택(`ip -j` JSON 사용, lo 제외)
    - 스냅샷 모델과 DoctorEvaluator
    - Planner(SHA-256 계획 해시)
    - InstallState/StateStore(잠금, 원자적 교체, .bak 보관, 손상된 파일은 초기화하지 않음)
    - CommandRunner(allowlist, 인수 배열, timeout·취소, UTF-16/UTF-8 판별, 비밀값 마스킹)
  - `src/WslRemoteSetup` (net10.0-windows, win-x64)
    - `WindowsProbe`: 레지스트리·WMI·NetworkInformation·방화벽 COM으로 조회하며 배포판은 기동하지 않음
    - 명령: `doctor [--json]`, `plan`, `status`, `help`, `version`
    - 대화형 메뉴: 비대화형으로 실행되면 종료코드 3(`UserInputRequired`)으로 끝남
  - `tests/WslRemoteSetup.UnitTests` (xUnit, 88개)
- G2 실기 결과(ownerAdmin 비승격)
  - doctor: 8초, exit 0. 실측값과 일치함
    - RDP 7389 PASS
    - `RDP change port` Private/LocalSubnet PASS
    - 기본 3389 Any 규칙 WARN
    - 1004 사용 가능
  - doctor 실행 후에도 Ubuntu-24.04는 **Stopped** 상태를 유지함(배포판을 깨우지 않음)
  - 발견·수정한 결함: 프로그램/UWP에 묶인 와일드카드 규칙이 RDP 허용 규칙으로 집계되던 오탐. 회귀 테스트 2개를 추가함.
  - 참고: WSL이 3.0.1.0에서 3.0.3.0으로 자동 업데이트됨(Store).

## 다음 단계

1. **M3 POC (코드·설계)** — 시스템 변경 없음
   - 소유자 에이전트와 네트워크 관리부 사이의 named pipe IPC 계약 설계: ACL, 호출자 SID 검증, 요청 스키마(installationId·observedWslIPv4만 허용)
   - 정책 검증기와 단위 테스트(변조된 요청, 임의 IP·포트 거부)
2. **M3 POC (실기)** — 승인 필요
   - 소유자 세션에서 WSL 수명 유지 방식 실험(foreground `wsl.exe` 관리)
   - 테스트 서비스 등록: 서비스 등록은 승인 대상
   - T09(일반 사용자 소유자) 검증용 일반 테스트 계정: 생성은 승인 대상. 생성하지 않으면 NOT_TESTED로 남긴다.
3. **M4** — 각 단계마다 승인 필요
   - OpenSSH 서버
   - portproxy
   - 방화벽 규칙
   - 각 단계는 Probe→Plan→Approval→Apply→Verify 흐름으로 구현한다.

## 미결·주의

- 로컬 git 저장소(`main`, 원격 없음)에 아직 커밋하지 않음. 저장소의 user.name/user.email이 설정되지 않음.
- systemd `degraded`(`systemd-binfmt` 실패)는 WARN으로 유지. SSH 구성 시 영향 여부를 다시 확인한다.
- 실제 sudo 비밀번호 인증, 외부 LAN SSH/RDP 인증은 NOT_TESTED.

## 진행 기록 요약

- **B3 (2026-10-17)**
  - 1차: 승격 `wsl --install --no-distribution` → exit 1. VMP만 적용됨. 출력 첫 줄 "액세스 거부"(원인 미확정).
  - 재부팅(승인됨, 21:57) 후 사용자가 직접 재실행 → WSL 3.0.1.0, 기본 버전 2 PASS.
  - WSL1 선택 구성요소는 Disabled로 둠(1.0 범위에 불필요).
- **B4 (2026-10-18 00:32)**
  - 비승격 `wsl --install -d Ubuntu-24.04 --no-launch` → exit 0. ownerAdmin HKCU에 등록됨.
  - 사용자가 `linux_admin`과 비밀번호를 직접 설정함.
  - 확인 결과: Ubuntu 24.04.5, uid 1000, sudo 그룹, systemd 사용, DNS·HTTPS 정상, eth0 172.24.220.111/20.
- **B5 (2026-10-18)**
  - winget으로 Git 2.55.0.5와 .NET SDK 10.0.402(런타임 10.0.13)를 설치함.
  - .NET 설치는 UAC 승인을 기다리느라 약 25분 지연됨.
- 제품 반영 사항(실측에서 확인)
  - wsl.exe 출력은 UTF-16LE이고 현지화되어 있다.
  - PS 5.1에서 `bash -lc "<복합 스크립트>"`로 인수를 넘기면 quoting이 깨진다.
  - WSL `lo`에 `10.255.255.254/32 scope global` 주소가 있다.
  - 종료코드 1이 "부분 성공 + 재부팅 필요"를 뜻할 수 있다.

## 이번 세션 수정 파일

- 루트: `.gitignore`, `.gitattributes`, `global.json`, `Directory.Build.props`, `WslRemoteSetup.slnx`
- `src/WslRemoteSetup.Core/**`, `src/WslRemoteSetup/**`, `tests/WslRemoteSetup.UnitTests/**`
- `docs/IMPLEMENTATION_STATUS.md`, `docs/TEST_RESULTS.md`, `docs/adr/0001-*.md`, `docs/adr/0002-*.md`
- `.local/` 보고서·로그(Git 제외)

## 참고

- 프로젝트 실제 경로는 `C:\project\wsl-remote-setup`이다. 문서가 제안한 `C:\projects\…`와 다르지만 현재 경로를 유지한다.
