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
| 후속 범위 | **부분 완료** | 여러 서비스 포트·고정 IP 마법사 코드 완료(실기 미검증). 로그인 전 접속: G2 외부 LAN SSH **PASS**(22:00 재시험) → 설치 선택 기능(기본 아니오, ADR 0010) 코드 완료 |
| 다음 | **진행** | ADR 0009: ① `export-client` G2 실기 PASS ② `client` 코드 완료(노트북 실기 대기) ③ win-arm64 게시 PASS(ARM 실행 NOT_TESTED) ④ 절전 점검·`keep-awake` G2 PASS. 설치본 최신화(a5a341d, 2026-10-19 00:26). G2 OS는 25H2 26200으로 업데이트됨 |
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

## 설치 매뉴얼 (2026-10-19)

- `tools/ManualBuilder`: 매뉴얼 pptx 생성 도구다. 제품 솔루션 밖에 있고, 개발 PC에서만 쓴다.
  - OOXML을 직접 쓰고 OpenXML SDK로 스키마를 검증한다(오류 0건).
  - 대략적인 미리보기 PNG를 그려 글자 넘침을 점검한다(넘침 0건).
- 생성 명령: `dotnet run --project tools/ManualBuilder -- artifacts/manual/WSL_Remote_Setup_설치매뉴얼.pptx [미리보기폴더]`
- 배포 묶음: `artifacts/release/`에 `WslRemoteSetup.exe`(e420717)와 매뉴얼(14장)이 있다.
- 이 PC에 PowerPoint와 LibreOffice가 없어 실제 PowerPoint 렌더링은 확인하지 못했다(NOT_TESTED).
- 다음: 사용자가 매뉴얼대로 G2에서 되돌리기 → 재설치를 직접 시험한다.

## 공개·배포 (2026-10-19)

- 저장소: https://github.com/edubeom/wsl-remote-setup (공개, MIT)
  - 공개 전에 커밋 기록 전체의 실제 값을 예시 값으로 바꿨다. 대응표는 `.local/REAL_VALUES.md`에 있다.
- 릴리스: v0.1.0 (커밋 57f226c), 파일 5개. GitHub 쪽 SHA-256 값이 로컬 빌드와 모두 일치한다.
  - `WslRemoteSetup-0.1.0-win-x64.exe`
  - `WslRemoteSetup-0.1.0-win-arm64.exe`
  - `WSL_Remote_Setup_Manual_KO-0.1.0.pptx`
  - `WSL_Remote_Setup_Manual_EN-0.1.0.pptx`
  - `SHA256SUMS.txt`
- 참고: GitHub는 첨부 파일 이름의 한글을 지운다. 첨부 파일 이름은 영문으로 짓는다.
- 다음 할 일:
  - 사용자가 매뉴얼대로 G2 되돌리기 → 재설치를 직접 시험한다.
  - 노트북 client 모드를 실기에서 확인한다.
  - M7(깨끗한 PC)에서 전체 과정을 확인한다.

## 서비스 포트 관리 (2026-10-20, ADR 0011)

- 코드를 완료했다(867c4cb).
  - 메뉴 7 / `ports [list|connect]`: 목록, 추가(WSL 열린 포트 찾기, 127.0.0.1·DB 경고), 삭제, 지금 연결.
  - 간격 제한 중에도 빠진 서비스 통로를 채운다.
  - status와 서비스 시작 기록에 "WSL 서비스 응답 여부"를 표시한다.
- 테스트: 단위 325, 통합 25 통과.
- G2 읽기 전용 확인 PASS: `ports list`, `ports connect`(요청 채널, 관리자 권한 없이 "모두 이미 연결"), WSL `ss` 실제 출력 해석.
- 실기 대기(승인 필요):
  - 추가: 관리자 창, portproxy·방화벽 변경.
  - 노트북에서 접속 확인.
  - 재부팅 후 유지 확인.
  - 삭제.

## 유튜브 발표 자료 (2026-10-20)

- `tools/ManualBuilder --talk`: 40장으로 구성했다.
  - 설명 슬라이드: 왜 리눅스, WSL, Windows Pro, 접속, 서비스 포트, 되돌리기, 정리
  - 실제 화면 기록(공개용 치환) 16장, 노트북 브라우저 화면 1장(주소는 예시 값으로 가림)
- 슬라이드별 발표 대본(`발표_대본.md`)도 함께 만든다.
- 생성물: `artifacts/talk/`(Git 제외). 스키마 오류 0, 넘침 0, 실제 값 0.
- 이미지 넣기(`Slide.Image`)를 추가했다. 화면 기록 생성을 `LoadPages`, `RenderPage`, `Finish`로 나눠 다시 쓸 수 있게 했다.
