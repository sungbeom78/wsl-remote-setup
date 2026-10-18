# 구현 현황

최종 갱신: 2026-10-18 (세션 1, M2 완료 시점)

## 단계 요약

| 단계 | 상태 | 비고 |
|---|---|---|
| M0 G2 진단·현황·변경 계획 | **완료** | `.local/G2_ENVIRONMENT_REPORT.md`, `.local/G2_ENVIRONMENT.json` |
| M1 WSL·Ubuntu·개발 도구 | **완료** | B3 WSL2 PASS, B4 Ubuntu-24.04/linux_admin PASS, B5 Git 2.55 + .NET SDK 10.0.402 PASS |
| M2 솔루션·doctor·plan·상태·단위 테스트 | **완료** | 빌드 경고 0, 단위 테스트 88/88 통과, G2 실기 doctor/plan/status 확인 |
| M3 권한 분리·WSL 수명 POC | **진행 중** | 코드·테스트 완료(단위 135, 통합 5 통과). WSL 수명 실기 POC PASS(keeper 방식). 서비스 컨텍스트·일반 계정 POC는 승인 대기 |
| M4 SSH·포워딩·방화벽 | **진행 중** | WSL OpenSSH(사용자 sudo) PASS, `forward apply`(portproxy 1004→22, LAN 한정 규칙) G2 로컬 검증 PASS. 다른 LAN PC 비밀번호·공개키 인증 PASS. 비밀번호 로그인은 사용자 결정으로 유지(ADR 0005), 제품에는 `--ssh-auth` 선택 옵션. Windows 계정·RDP는 기존 값을 유지하므로 변경 없음 |
| M5 자동 실행·복구·pause·제거 | **진행 중** | 설치·pause/resume(T16)·재부팅 후 복구(T15: 로그인 전 수신 자가 복구 + 로그인 후 에이전트) PASS. 남은 것: WSL 주소 변경(T11) 실기, autostart remove 실기 |
| M5+ 사용자 흐름 | **코드 완료** | 설치 질문(포트 등, Enter=기본값)·포트 추천, DHCP 경고·주소 따라가기(ADR 0007), 통합 설치 마법사 `install`/`resume`, 단계별 되돌리기 `uninstall`, 쉬운 status, 문의 채널. 실기: 신규 PC(M7)에서 전 과정 확인 필요 |
| 후속 범위 | **부분 완료** | 여러 서비스 포트·고정 IP 마법사 코드 완료(실기 미검증). 로그인 전 무인 실행 POC는 G2 내부 경로 PASS·외부 LAN FAIL로 **종료**(1.0은 로그인 후 범위 유지) |
| 다음 | **진행** | ADR 0009: ① `export-client` G2 실기 PASS ② `client` 코드 완료(노트북 실기 대기) ③ win-arm64 단일 EXE 게시 PASS(실행은 ARM 장비 없음 NOT_TESTED) ④ 절전 점검 status·`power` 코드 완료, G2 읽기 PASS(`keep-awake` 적용은 승인 대기). 단위 294·통합 25 통과 |
| M6~M7 | 대기 | M7: 깨끗한 Windows에서 EXE 하나로 install → status → uninstall |

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

## M3 코드 단계 결과 (2026-10-18)

- 커밋 `45efbb4`(M0~M2) 이후 작업. 설계는 `docs/adr/0003-owner-agent-network-manager-ipc.md`.
- Core
  - `Ipc/RepairMessages`: 엄격한 요청 파서(1 KB, 필드 3개 고정, 중복·미지 필드·타입 오류 거부), 짧은 응답 코드.
  - `Policy/ForwardingPolicy`: 0.0.0.0·loopback 수신 금지, 사용자 SID 필수.
  - `RepairRequestValidator`: 호출자 SID = 소유자, 설치 ID, 사설 IPv4, 관리부가 직접 관찰한 WSL 대역 안, rate limit.
  - `Forwarding/PortProxyReconciler`: 소유한 tuple만 다룸. Add/NoChange/Update를 결정하고, 외부 변경·와일드카드 수신이 있으면 Conflict로 판정.
  - `RepairCoordinator`: 검증 → 조정 → endpoint 확인 → 적용. Windows 측은 `IForwardingHost`로 추상화.
- App `Ipc/RepairPipe`
  - ACL: SYSTEM·Administrators FullControl, 소유자 ReadWrite, NETWORK Deny, 상속 차단.
  - 서버: `FirstPipeInstance`, `RunAsClient`로 호출자 SID 확인, 5초 읽기 timeout, 초과 메시지는 버린 뒤 거부(64 KB를 넘으면 연결을 끊음).
  - 클라이언트: 가장 수준을 Identification으로 제한.
- 테스트
  - 단위 131개 통과.
  - 통합(`tests/WslRemoteSetup.IntegrationTests`) 5개 통과, 3회 반복해도 결과가 같음.
    - 다루는 항목: 같은 사용자 파이프 왕복, 변조 요청, 초과 요청, ACL 거부, 보안 설명자.
  - 1차 실행에서 결함 1건 발견 후 수정: 초과 요청 처리 시 교착이 생겼다. 이제 서버가 메시지 끝까지 읽어 버린 뒤 응답한다.
- 미검증: 다른 로컬 사용자 거부(T10 실계정), 서비스 컨텍스트(SYSTEM)에서의 가장, 파이프 선점(squatting) 방지의 실효성, 클라이언트 측 서버 신원 확인.

## M3 실기: WSL 수명 POC (2026-10-18)

- `Lifetime/WslLifetimeKeeper`: 셸 없이 `wsl.exe --distribution <d> --exec /bin/sleep infinity` 클라이언트 하나를 보유한다. Release는 자기 프로세스만 종료한다.
- `poc-lifetime` 명령: 개발용이며 help에는 표시하지 않는다.
- `Core/Wsl/WslOutputParser`: `--list --running --quiet`를 현지화 문구와 무관하게 해석한다.
- 결과: 클라이언트가 없으면 16초 뒤 유휴 종료된다. keeper를 보유한 120초 동안 계속 Running이었고 주소도 유지됐다. 해제하면 16초 뒤 종료된다. 상세는 ADR 0003과 TEST_RESULTS에 있다.

## 다음 단계

1. **M3 POC (실기, 남은 부분)** — 승인 필요
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

## T15 재개 지점 (2026-10-18)

- 사용자가 재부팅을 승인했다. 재부팅 후 ownerAdmin이 RDP로 재접속하면 다음을 읽기 전용으로 확인한다: LastBoot, 서비스 상태, 로그온 작업 실행 기록, 에이전트 로그·상태, 실행 중인 배포판, portproxy 대상과 WSL 실제 주소(같은지, 바뀌었으면 자동 갱신됐는지 = T11), 1004 경유 SSH 배너.

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
