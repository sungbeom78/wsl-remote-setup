# G2 개발 시작 기획서 — WSL Remote Setup

> 버전: 1.0 · 작성일: 2026-10-17 · 대상: G2에서 작업하는 Claude Code
>
> 이 문서는 **개발 착수용 요구사항·설계 기준**이다. G2에 접속해 검사하거나, WSL을 설치하거나, EXE를 빌드한 결과 보고서가 아니다. 문서의 예상 환경값은 반드시 현장에서 확인한다.

## 0. 가장 먼저 읽을 결론

**만들 제품:** Windows PC에 Ubuntu WSL 2와 원격접속 환경을 구성하고, 로그인 이후 WSL 내부 IP가 달라져도 접속 경로를 다시 연결해 주는 단일 배포 EXE.

**개발 장소:** G2. 사용자의 현재 설명에 따라 **Windows만 설치되어 있고 Ubuntu WSL은 미설치인 상태**에서 시작한다.

**작업 순서:** Windows에 Claude Code를 먼저 설치 → 읽기 전용 진단 → 운영 Windows 계정 결정 → 승인받아 WSL/Ubuntu 초기 구성 → 개발 도구와 제품 구현 → G2 통합 테스트 → 별도의 깨끗한 Windows 환경에서 신규 설치 검증.

**핵심 구분:**

- G2를 개발 가능한 상태로 만드는 초기 설정과, 다른 PC에 배포할 제품 구현은 별도 작업이다.
- 개발용 Claude Code·Git·.NET SDK는 최종 사용자 PC의 필수 설치 항목이 아니다.
- WSL을 실행할 Windows 계정과 관리자 작업을 수행할 계정은 구분한다.
- 같은 LAN의 다른 PC에서 접속하는 것과 인터넷에서 접속하는 것은 구분한다.
- 로그인 자동 실행과 부팅 직후 무인 실행은 구분한다.
- “검사 명령 성공”, “포트 연결 성공”, “실제 SSH/RDP 로그인 성공”은 각각 별도로 기록한다.

---

## 1. 배경과 목표

사용자는 Windows 미니 PC를 원격 개발·실습 서버로 구성할 때 반복되는 수작업을 줄이려 한다. WSL 설치, Linux 계정 생성, SSH 설치, Windows 원격접속 계정, RDP, 방화벽, Windows→WSL 포트포워딩, 로그인 후 연결 복구를 하나의 흐름으로 제공한다.

### 1.1 사용자 경험

사용자는 `WslRemoteSetup.exe` 하나를 실행한다. 프로그램은 현재 상태를 먼저 검사하고, 필요한 질문에 기본값을 제시한다. 일반 설정은 Enter로 진행할 수 있지만, 비밀번호와 위험한 변경은 별도 입력·승인을 요구한다.

설치 이후에도 같은 EXE로 상태 확인, 연결 복구, 설정 변경, 자동 실행 일시중지, 도구가 만든 설정의 제거를 수행한다.

### 1.2 성공의 정의

1. Windows만 설치된 지원 PC에서 설치를 시작할 수 있다.
2. 사용자 승인을 거쳐 Ubuntu WSL 2와 SSH/RDP 환경이 구성된다.
3. 선택한 Windows 운영 계정으로 로그인하면 대상 WSL과 SSH 접속 경로가 준비된다.
4. 대상 WSL의 내부 IP 변경 뒤에도 승인된 Windows 수신 주소·포트를 유지하며 포워딩 대상만 갱신한다.
5. 재실행·실패·재부팅 시 이미 완료한 작업을 확인하고 이어서 진행한다.
6. 기존 계정·배포판·다른 서비스의 네트워크 설정을 무단으로 덮어쓰지 않는다.
7. 실패 원인, 완료 범위, 미검증 범위, 복구 방법을 사용자가 이해할 수 있게 보여준다.

---

## 2. G2 환경: 알려진 값과 확인할 값

과거 다른 PC나 같은 이름의 과거 환경보다 **현재 사용자 설명과 G2 실측 결과**를 우선한다.

| 항목 | 현재 기준 | 처리 원칙 |
|---|---|---|
| 대상 장비 | G2 미니 PC | 다른 서버에 적용하지 않는다 |
| 현재 설치 상태 | Windows만 설치, Ubuntu WSL부터 필요 | 과거 WSL 설치 이력을 현재 상태로 간주하지 않는다 |
| Windows 에디션 | 이전 대화에서 Pro로 확인 | OS 버전·빌드·아키텍처와 함께 재조회한다 |
| 네트워크 | 이전 기록상 Wi-Fi | 활성 어댑터와 실제 경로를 확인한다 |
| Windows IPv4 | 이전 기록상 `192.168.1.222` | 예상값이며 실측 전 확정하지 않는다 |
| IP 설정 방식 | 사용자는 각 서버의 OS에서 직접 고정 IP 설정 | 공유기 DHCP 예약으로 임의 변경하지 않는다 |
| 기존 원격 계정 | `owneradmin` 생성 계획 및 원격접속 사용 이력 | 계정 존재·SID·그룹을 검사하며, 존재한다고 단정하지 않는다 |
| 기존 RDP 포트 | 실제 적용값 미확정 | 레지스트리·리스너·방화벽에서 확인한다 |
| 게이트웨이·DNS·서브넷 | 현장 확인 대상 | `.1`, `/24`, 특정 DNS를 임의 입력하지 않는다 |
| WSL 소유 Windows 계정 | 현장 진단 후 선택 | 배포판 등록 전에 결정한다 |
| 프로젝트 경로 | 제안: `C:\projects\wsl-remote-setup` | 기존 폴더가 있으면 내용 확인 후 사용한다 |

### 2.1 반드시 보존할 것

현재 G2의 IP, DNS, 게이트웨이, RDP 포트, 기존 계정 권한, 원격접속 세션은 기본적으로 보존한다. 진단값이 예상값과 다르다고 문서에 맞추기 위해 강제로 변경하지 않는다.

현재 원격 세션을 끊을 수 있는 조치는 실행 전 영향을 설명하고 승인받는다. G2에서 Wi-Fi 비활성화, 네트워크 초기화, 기존 RDP 포트 변경을 “일반적인 초기화”로 실행하지 않는다.

---

## 3. 범위와 기본 결정

### 3.1 1.0 MVP에 포함

| 영역 | 구현 내용 |
|---|---|
| 배포 | Windows x64용 인터넷 연결형 단일 EXE |
| 인터페이스 | 한국어 콘솔 마법사, Enter 기본값, 명확한 단계 번호 |
| 진단 | OS·권한·가상화·재부팅 상태·계정·WSL·네트워크·포트·방화벽 |
| WSL | WSL 런타임 설치, Ubuntu 24.04 LTS 초기화, 소유자 확인 |
| Linux | `linux_admin` 기본 제안, sudo 가능 여부 확인, OpenSSH 구성 |
| Windows 계정 | 기존 계정 사용 또는 신규 `remoteControl` 생성 |
| RDP | 지원 에디션 확인, 활성화, 허용 사용자·NLA·필요 방화벽 설정 |
| 네트워크 | 기존 Windows IP 보존, WSL 2 NAT 기반 TCP 포워딩 |
| 자동 실행 | 선택한 운영 계정 로그인 이후 실행·연결 복구 |
| 지속 실행 | 로그인 세션 안에서 WSL 수명 및 연결 상태 관리 |
| 복구 | 재부팅·재실행 이어하기, 중복 방지, 오류별 복구 |
| 제거 | 도구 소유 리소스만 제거, 사용자 데이터 기본 보존 |
| 결과 | 접속 안내문, 로컬 로그, 단계별 검증 결과 |

초기 검증 대상은 **Windows 11 Pro x64 + Ubuntu 24.04 LTS**로 제안한다. G2 실측 OS가 다르면 해당 차이를 먼저 보고하고 지원 표를 조정한다. Windows Home은 RDP 호스트 기능을 제공하지 않으므로 SSH-only 분기로 처리한다. 다른 에디션/빌드의 지원은 테스트 통과 후 표기한다. [S06]

Ubuntu 24.04는 이번 제품의 **초기 검증 기준 버전**이지, “현재 최신 Ubuntu”라는 뜻이 아니다. 온라인 배포판 목록에서 정확한 식별자를 확인한다. [S02]

### 3.2 후속 범위

- Windows 고정 IPv4를 신규 설정하는 고급 마법사: 충돌 확인, DHCP 풀 충돌 가능성 안내, 복구 경로, 사용자 확인을 포함한다.
- 로그인 전 무인 부팅 모드: 실행 계정·자격증명·프로필·WSL 수명 검증 후 제공한다.
- 미러 네트워킹, IPv6, 여러 배포판, ARM64, 복수 서비스 포트.
- GUI, 서명·업데이트 배포 체계, 오프라인 설치 묶음.

**주소 유지 요구를 삭제한 것이 아니다.** 1.0은 이미 고정된 Windows 주소를 보존하고 WSL 내부 IP 변화를 흡수한다. Windows가 DHCP이면 `Windows 주소 고정: 미완료`로 표시한다. 고정 설정 또는 별도 주소 유지 수단을 확인하기 전에는 “항상 같은 주소”라고 보장하지 않는다.

### 3.3 1.0에서 자동화하지 않는 것

공유기 관리, UPnP 활성화, 공인 IP 고정, DDNS 계정 생성, VPN 가입·설치, 인터넷 RDP 직접 공개, Windows 보안 정책 우회, BIOS 변경, 비밀번호 없는 자동 로그인은 제외한다.

인터넷 접속을 선택하면 LAN 구성을 완료한 뒤 **별도 네트워크 작업과 실제 외부 검증이 남았음**을 표시한다. 공유기 설정 없이 인터넷 접속까지 완료했다고 출력하지 않는다. LAN 밖의 접속에는 VPN 또는 별도 포워딩 경로가 필요하다. [S06]

---

## 4. 기술 방향

### 4.1 권장 구성

- 제품: C# / .NET 10 LTS.
- Windows 호스트: `net10.0-windows`, 배포 대상 `win-x64`.
- 순수 정책·상태 전이: 별도 라이브러리로 분리해 시스템 변경 없이 테스트한다.
- Windows 작업: 가능하면 .NET/Windows API, 필요한 부분은 검증된 PowerShell 또는 Windows 기본 명령을 제한적으로 사용한다.
- Linux 작업: 고정된 Bash 스크립트와 구조화된 입력. 사용자 문자열을 셸 코드에 그대로 이어 붙이지 않는다.
- 상태 저장: 버전이 있는 JSON, 파일 기반 로그. 초기 버전에는 DB·웹서버·Docker를 넣지 않는다.
- UI: 콘솔부터 완성한다. 단일 EXE 요구를 GUI 요구로 확대 해석하지 않는다.

2026-10-17 확인 기준 .NET 10은 지원 중인 LTS다. 구현 시작 시 설치한 SDK의 정확한 버전을 기록하고 `global.json`에 재현 가능한 정책을 고정한다. 최신 프리뷰를 기본으로 선택하지 않는다. [S11]

### 4.2 Windows에서 개발하는 이유

Claude Code는 Windows 네이티브로 실행할 수 있으므로 WSL 준비 전부터 개발 환경을 점검할 수 있다. Windows 네이티브 도구를 다루는 이 프로젝트에서는 Windows를 주 개발·빌드·통합 테스트 환경으로 사용한다. Git for Windows는 저장소 관리와 Bash 도구 사용을 위해 설치를 권장하지만, 최신 공식 문서상 Claude Code의 필수 선행 조건으로 단정하지 않는다. [S01]

WSL 내부 Claude 설치는 선택 사항이다. 먼저 Windows 쪽 작업 흐름을 완성하고, Linux 작업이 필요하면 대상 배포판을 명시해 `wsl.exe`로 호출한다. WSL 테스트 결과만으로 Windows 계정·UAC·RDP·방화벽 테스트를 통과 처리하지 않는다.

### 4.3 개발 프레임워크

이 요청에서 특정 `dev-process-*` 저장소의 적용은 지정되지 않았다. 임의로 프레임워크를 다운로드하거나 현재 프로젝트를 새 프레임워크로 덮어쓰지 않는다. 기존 프로젝트 지침이 존재하면 읽고, 충돌 없는 범위에서 통합한다. Git 원격 저장소 생성·push는 별도 요청 없이 하지 않는다.

---

## 5. G2 초기 설정 작업: 제품 개발과 분리

### B0. Claude Code 진입

사용자가 Windows에서 Claude Code를 설치하고 로그인한다. 정상 동작 확인 후 프로젝트 폴더에서 시작한다. 설치 명령과 시작 프롬프트는 루트 `START_HERE.md`에 있다.

Claude Code는 평소 일반 권한으로 실행한다. 운영체제 변경 시에만 별도 관리자 작업을 요청한다. 최초부터 Claude 전체를 무제한 관리자 권한으로 운영하거나 권한 확인을 우회하지 않는다.

### B1. 읽기 전용 진단과 작업 기록 준비

프로젝트의 기존 파일·지침·Git 상태를 먼저 확인한다. 환경 스냅샷은 `.local/`에 두고 Git에서 제외한다. 공유용 보고서는 계정 SID·내부 IP 등 식별 정보를 필요에 따라 가린다.

진단 항목:

- OS 버전, 빌드, 에디션, 아키텍처, 메모리와 디스크 여유.
- 현재 Windows 계정과 SID, 프로세스 권한, 콘솔/RDP 세션 여부.
- `owneradmin` 등 기존 관련 계정의 존재 여부와 필요한 그룹 구성.
- Windows 선택 기능, 가상화 가능 여부, WSL 설치 여부, 재부팅 필요 상태.
- 현재 계정에서 확인 가능한 배포판과 WSL 버전. 다른 계정의 배포판까지 검사했다고 주장하지 않는다.
- 활성 어댑터, IPv4와 접두 길이, DHCP/수동 여부, 기본 경로, DNS, 네트워크 프로필.
- 현재 RDP 포트·NLA·허용 상태와 관련 방화벽 규칙.
- TCP 1004 충돌, 기존 portproxy, 제외 포트 범위, IP Helper 서비스 상태.
- `claude`, `git`, `dotnet`, `winget` 가용 여부.

각 명령의 종료코드와 조회 실패를 구분한다. 권한 부족을 “미설치”로 해석하지 않는다. WSL 미설치 상태에서 조회 명령이 설치를 유도하는 경우, 자동 승인하지 말고 종료해 다음 승인 단계로 넘긴다.

**첫 산출물:** `.local/G2_ENVIRONMENT_REPORT.md`, `.local/G2_ENVIRONMENT.json`, `docs/IMPLEMENTATION_STATUS.md`.

### B2. 운영 Windows 계정 결정

가능하면 사용자가 이미 사용하는 원격 계정을 재사용한다. `owneradmin`이 실제로 있고 사용 목적에 맞으면 우선 제시하되 강제로 선택하지 않는다. 새 계정이 필요할 때만 `remoteControl`을 기본 제안한다.

**배포판 등록 전에 최종 소유자의 SID를 확정한다.** WSL 배포판과 Linux 계정은 Windows 계정별 설치 구조이며, 현재 관리자에게 설치한 배포판이 새 Windows 계정에 자동으로 공유되지 않는다. [S03]

소유자 계정이 현재 세션과 다르면 해당 계정의 로그인·프로필 초기화를 사용자에게 요청하고 `AwaitingOwnerLogin`에 멈춘다. 이 경계를 숨기려고 평문 Windows 비밀번호 저장이나 자동 로그인을 도입하지 않는다.

### B3. Windows WSL 런타임 설치

사용자에게 변경 내용·재부팅 가능성·현재 원격접속 영향 범위를 보여준다. 승인 후 관리자 작업으로 WSL 런타임을 설치한다.

공식 명령 기준 예시:

```powershell
# Windows 관리자 PowerShell. 실제 지원 옵션 확인 및 승인 후 실행.
wsl --install --no-distribution
```

`--no-distribution`은 WSL 미설치 상태에서 배포판 설치를 분리하는 옵션이다. 실제 지원 여부와 종료 상태를 확인하고, 명령 성공만으로 준비 완료라고 기록하지 않는다. [S02]

재부팅이 필요하면 상태와 다음 작업을 저장하고 사용자에게 재부팅을 요청한다. 재접속 후 실제 기능 상태를 다시 검사한다. **사용자 승인 없이 재부팅하지 않는다.**

### B4. 소유자 계정에서 Ubuntu 초기화

선택한 Windows 운영 계정의 일반 세션에서 실행한다. 다른 관리자 계정의 승격 세션 안에서 무작정 배포판을 설치하지 않는다.

```powershell
# 선택된 Windows 소유자 세션. 조회 후 해당 식별자를 사용할 수 있을 때만 진행.
wsl --list --online
wsl --install -d Ubuntu-24.04 --no-launch
wsl -d Ubuntu-24.04
```

배포판 이름이 다르거나 이미 존재하면 이를 확인해 기록한다. 최초 Linux 계정은 `linux_admin`을 제안하고, 비밀번호는 사용자가 터미널에서 직접 입력한다. 명령을 복사해 한꺼번에 실행하지 않으며 설치·초기화·검증을 나눈다. [S02][S03]

검증: 소유자 SID, 실제 배포판 이름, WSL 2 여부, `/etc/os-release`, Linux 기본 사용자, home, sudo 가능 여부, 인터넷/DNS, PID 1과 systemd 상태.

WSL 1이거나 기능이 부족하면 진단 결과를 보고한다. 기존 배포판의 변환·초기화·삭제는 별도 승인 없이는 하지 않는다.

### B5. 개발 도구와 저장소 기반

Windows에 Git과 .NET 10 SDK를 확인·설치한다. WinGet 사용이 가능한 경우 공식 .NET 설치 문서의 패키지를 사용한다. [S12]

```powershell
# 설치는 사용자 승인 후, 이미 설치되어 있으면 검사만 한다.
winget install --id Microsoft.DotNet.SDK.10 --exact --source winget
```

`dotnet --info`, `git --version`, `claude --version` 결과를 기록한다. 패키지 소스 접근 실패 시 보안 설정을 낮추지 말고 공식 설치 경로를 안내한다.

프로젝트 폴더에 `.gitignore`, `.gitattributes`, 솔루션, 테스트 프로젝트를 생성한다. Bash 파일은 LF, 상태 JSON과 Markdown은 UTF-8로 관리한다. Windows PowerShell 5.1이 파일을 읽는 지점에서는 인코딩을 명시하고 한글 경로·로그를 테스트한다.

### B6. SSH와 연결 설정을 제품의 첫 구현으로 진행

여기까지는 G2 부트스트랩이다. 이제부터 SSH 설치·방화벽·포워딩을 별도 일회성 명령만으로 끝내지 말고 **제품의 진단→계획→승인→적용→검증 흐름**으로 구현해 G2에 적용한다.

긴급 진단용 수동 조치가 필요하면 실행 내역과 변경 전후 상태를 남기고, 동일 동작을 제품 테스트에 반영한다.

---

## 6. 제품 설치 마법사

### 6.1 실행 화면

```text
WSL Remote Setup

1. 새로 설치 / 기존 환경에 추가 설정
2. 현재 상태 확인
3. 연결 복구
4. 설정 변경
5. 자동 실행 일시중지 / 재개
6. 이 도구가 만든 설정 제거
0. 종료

선택 [1]:
```

더블클릭 실행 시 오류 직후 창을 닫지 않고 결과·로그 위치를 보여준다. 비대화형 실행에서 입력이 필요하면 무한 대기하지 않고 `UserInputRequired`를 반환한다.

### 6.2 입력값과 기본 동작

| 항목 | 기본값 / 규칙 |
|---|---|
| 기능 | WSL SSH + Windows RDP. SSH-only 선택 가능 |
| 접속 범위 | LAN. 인터넷 선택 시 별도 작업·미검증 표시 |
| 운영 Windows 계정 | 적합한 기존 계정 우선; 신규 계정은 `remoteControl` |
| Windows 비밀번호 | 신규 계정은 설치 중 직접 입력·확인. 공통 기본값 없음 |
| 관리자 그룹 추가 | 기본 아니요. 기존 그룹은 무단 변경하지 않음 |
| 배포판 | 검증 대상 `Ubuntu-24.04`, 실제 식별자 검사 |
| Linux 사용자 | `linux_admin` |
| Linux 비밀번호 | 사용자 직접 입력. Windows 비밀번호와 별개 |
| SSH 인증 | 공개키 우선. 공개키 없으면 키 등록 대기 또는 승인된 LAN 비밀번호 방식 |
| Windows 수신 주소 | 선택한 실제 어댑터의 기존 IPv4 유지 |
| Windows SSH 수신 포트 | TCP `1004`. 충돌하면 변경 질문 |
| WSL SSH 포트 | TCP `22`. 기존 SSH 포트는 확인 후 재사용 가능 |
| Windows RDP 포트 | 기존값 유지; 신규 기본값은 `3389` |
| 방화벽 원격 범위 | 선택된 신뢰 LAN의 실제 CIDR 또는 사용자가 지정한 접속 PC |
| 자동 실행 | 선택한 운영 계정 로그인 이후 |
| 절전 변경 | 기존값 유지. 전원 연결 시 서버용 설정은 별도 승인 |
| 재부팅 | 자동 실행하지 않음. 재개 안내 후 사용자 선택 |

`192.168.x.79`, 특정 게이트웨이, 모든 어댑터의 `0.0.0.0` 수신을 기본으로 넣지 않는다.

### 6.3 승인 정책

“각 단계 승인”은 **해당 단계가 바꿀 설정을 이해한 뒤 진행하는 것**이다. 수십 개의 내부 명령마다 같은 질문을 반복할 필요는 없다.

- 진단, 계획 생성, 프로젝트 안의 코드·테스트 수정: 별도 OS 변경 승인 없이 진행 가능.
- 계정 생성, Linux 패키지 설치, SSH 설정, RDP/방화벽 변경, 자동 실행 등록: 단계별 변경 요약 후 승인.
- 관리자 권한 부여, 네트워크 변경, 공개 범위 확대, 서비스 재시작, WSL 종료, 재부팅, 제거: 명시적 승인. 기본 거부.
- 최종 적용 화면은 변경 전→변경 후와 영향을 표시한다. Enter만으로 위험 변경에 동의한 것으로 처리하지 않는다.
- 승인 후 계획이 바뀌면 새 계획에 대해 다시 승인받는다.

### 6.4 비밀번호 입력 경계

비밀번호를 채팅에 적게 하지 않는다. 제품의 마스킹 입력 또는 사용자가 직접 조작하는 OS/Linux 입력창을 사용한다. 인증정보를 명령 인수, 환경 변수, Git, 로그, 상태 JSON, 재개 파일에 저장하지 않는다.

최초 RDP 접속 때 비밀번호를 만들겠다는 방식 대신 계정 생성 시 설정한다. 기존 계정의 암호를 임의로 재설정하지 않는다. PIN을 원격 로그인 비밀번호로 간주하지 않는다. RDP에는 허용 사용자와 정상 인증이 필요하고 NLA를 유지한다. [S06]

---

## 7. 기능 요구사항

### FR-01. 진단과 계획

진단은 부수효과 없는 경로로 구현한다. `doctor`에서 WSL 실행이 필요하다면 읽기 전용 결과와 별도 동작을 구분하고 승인받는다. 중지된 배포판을 몰래 깨우지 않는다.

계획은 각 작업마다 현재 상태, 목표 상태, 변경 필요 여부, 실행 주체, 권한, 재부팅·접속 단절 가능성, 되돌릴 수 있는 범위를 포함한다.

### FR-02. Windows 계정과 RDP

계정은 이름뿐 아니라 SID로 식별한다. 현지화된 그룹 표시 이름을 하드코딩하지 말고 Windows가 제공하는 안정적인 식별자를 사용한다. RDP 접속 허용을 위해 신규 사용자를 무조건 Administrators에 넣지 않는다.

기존 RDP 포트는 보존한다. RDP 포트 변경은 1.0의 기본 흐름에 넣지 않으며 필요 시 별도 고급 작업으로 제안한다. 관리 정책이 RDP나 방화벽을 통제하면 우회하지 말고 제한 사항을 출력한다.

Windows Home에서 RDP를 가능하게 한다며 패치·우회 도구를 설치하지 않는다. [S06]

### FR-03. WSL 런타임과 배포판

Windows 기능 설치와 사용자별 배포판 등록을 분리한다. 배포판 관련 모든 호출에 명시적인 소유자와 실제 배포판 이름을 사용한다.

`wsl -l -v` 결과가 비어 있더라도 현재 호출 계정만의 결과임을 기록한다. 관리자 또는 SYSTEM 세션에서 목록이 비었다고 소유자 배포판을 재설치하지 않는다. [S02][S03]

다운로드 실패, 가상화 미활성화, 권한 부족, 정책 차단, 디스크 부족, 재부팅 필요를 다른 오류로 분류한다. 기존 데이터에 대한 `wsl --unregister`는 설치·복구 흐름에서 금지한다.

### FR-04. Linux 사용자와 SSH

소유자 Windows 세션에서 Linux 사용자를 확인·생성하고 OpenSSH를 설치한다. root SSH 로그인을 금지하고 선택한 계정만 허용하는 정책을 계획에 포함한다. 공개키는 클라이언트에서 생성된 공개키만 받으며 개인키를 수집하지 않는다.

기존 `authorized_keys`는 보존·중복 제거한다. 도구가 추가한 키의 지문을 별도 기록해 제거 시 해당 키만 다룬다. SSH host key는 기존 것을 보존하고 공개 지문만 연결 안내에 남긴다.

설정은 독립적인 drop-in을 우선 검토하되 파일 이름만으로 적용 우선순위를 단정하지 않는다. Ubuntu OpenSSH는 설정의 첫 적용값과 Include 위치가 중요하므로 `sshd -t`, 필요 시 접속 조건을 포함한 `sshd -T`로 실제 유효값을 확인한다. [S08]

Ubuntu 24.04의 실제 `ssh.service`·`ssh.socket` 상태를 확인한다. 서비스 이름이나 socket 활성화 상태를 추측하지 않는다. 구문 검사와 유효값 검증이 통과한 뒤 반영하고, 실제 TCP 22 리스너와 host key를 확인한다.

비밀번호 로그인을 끄기 전 공개키 인증을 별도 클라이언트에서 검증한다. 아직 키가 없으면 설치를 `AwaitingAuthenticationSetup`에 두거나 사용자가 승인한 LAN 비밀번호 방식을 쓴다. “키도 없고 비밀번호도 막혀서 접속 불가”인 상태를 정상 완료로 처리하지 않는다.

### FR-05. Windows→WSL 포워딩

```text
접속 클라이언트
  └─ Windows의 승인된 LAN IPv4:1004
        └─ 현재 대상 WSL IPv4:22
              └─ linux_admin SSH 인증
```

Windows portproxy는 TCP 대상으로 사용한다. UDP나 인터넷 공유기 포워딩을 해결하는 기능으로 설명하지 않는다. [S04][S07]

WSL 주소 선택은 단순히 출력의 첫 토큰을 쓰지 않는다. 대상 배포판의 기본 경로와 인터페이스별 IPv4를 구조화해 확인하고 loopback·Docker 등의 다른 주소를 배제한다. WSL NAT 대역과 실제 SSH endpoint를 검증한 뒤 적용한다.

수신 어댑터·IP·포트는 승인된 값만 사용한다. 네트워크가 바뀌어 기존 수신 IP가 사라져도 다른 어댑터나 전체 인터페이스로 자동 확대하지 않는다.

동일 규칙이 있으면 생성하지 않는다. 소유권 없는 규칙과 충돌하면 정지한다. 갱신 전 규칙을 보관하고 자신이 관리하는 tuple만 수정한다. 전체 `portproxy reset`은 금지한다.

IP Helper 및 관련 네트워크 상태를 진단하되 IPv6나 보안 기능을 통째로 꺼서 우회하지 않는다.

### FR-06. 방화벽

신뢰하는 인터페이스·네트워크 프로필·수신 주소·서비스 포트·원격 CIDR을 조합해 최소 범위를 계획한다. CIDR은 실제 어댑터 설정에서 계산하며 `/24`로 단정하지 않는다.

현재 프로필이 Public이면 이를 숨기거나 모든 프로필에 Allow 규칙을 만들지 않는다. 현 네트워크를 신뢰할지 사용자에게 묻고, 필요 시 별도 변경 계획을 보여준다.

도구가 추가하는 규칙은 고유 ID로 기록한다. 기존 광범위한 RDP/SSH Allow 규칙이 남아 있으면 “LAN 제한 완료”로 오인하지 않도록 경고한다. 좁은 규칙을 추가한 것만으로 다른 넓은 허용 규칙이 취소되는 것으로 처리하지 않는다.

WSL/Hyper-V 및 Linux 방화벽이 경로에 관여하는 경우 실제 상태를 검사한다. 기능 지원 차이로 추가 규칙이 필요하면 포트·범위를 한정한다. 전체 inbound 허용이나 방화벽 비활성화는 금지한다. [S04]

### FR-07. 로그인 자동 실행과 연결 복구

대상 Windows 소유자 SID의 로그인에 연결한 작업을 등록한다. 사용자 비밀번호를 도구 파일에 저장하지 않는 대화형 로그온 모드를 기본으로 한다. 작업 스케줄러의 실행 주체·로그온 유형·권한 수준은 별개다. [S05]

자동 실행은 다음을 수행한다.

1. 해당 소유자의 실행 중인 관리 에이전트 중복 여부 확인.
2. 승인된 대상 WSL 실행 및 사용자 세션 안에서 수명 관리.
3. SSH 서비스/리스너/host key 준비 상태 확인.
4. 검증한 WSL IP와 현재 포워딩 비교.
5. 승인된 endpoint에 한정해 필요한 부분만 갱신.
6. 상태 기록, 이후 제한된 주기의 재검사와 네트워크 복구 처리.

systemd로 SSH가 시작되는 것과 WSL 인스턴스의 수명이 유지되는 것은 별개다. foreground `wsl.exe` 프로세스를 관리하는 방식 등을 POC에서 확인하고, systemd 서비스 하나로 항상 살아 있다고 가정하지 않는다. [S09][S10]

상태 점검 기본 제안은 60초이며 실패 시 backoff와 상한을 둔다. 계속 바뀌지 않는 설정을 매번 재작성하지 않는다. 한 번에 한 인스턴스만 실행한다.

사용자가 `pause`를 선택하면 복구를 중지한다. 사용자가 별도로 `wsl --shutdown`을 실행했을 때 자동 복구가 재시작할 수 있음을 알리고, 유지보수 시 pause를 먼저 사용하게 한다. 서비스가 없는 상태를 무한 반복으로 고치려 하지 말고 일정 실패 후 경고한다.

RDP 창 닫기(연결 끊기), 잠금, Windows 로그아웃, Windows 재부팅을 구분해 시험한다. **1.0은 로그아웃 후·로그인 전 가용성을 보장하지 않는다.**

### FR-08. 상태·접속 안내·복구

결과는 `PASS / FAIL / WARN / PENDING / NOT_TESTED / NOT_APPLICABLE`로 구분한다. “모든 설정 완료” 대신 항목별 결과를 보여준다.

출력 예시 — 아래 주소는 G2에서 실측 확인된 경우에만 사용:

```text
WSL 런타임                         PASS
대상 배포판 소유자                  PASS
SSH 설정 및 WSL 내부 리스너         PASS
Windows 포워딩 경유 연결            PASS
다른 LAN PC의 실제 SSH 인증         NOT_TESTED
Windows RDP 실제 로그인             NOT_TESTED
인터넷 경로 및 인증                 NOT_TESTED
로그인 전 무인 동작                 NOT_APPLICABLE

SSH: ssh -p 1004 linux_admin@192.168.1.222
RDP: 192.168.1.222:<실측 RDP 포트>
```

접속 안내에 비밀번호를 포함하지 않는다. IP가 DHCP이면 변동 가능성을 표기한다. TCP 검사만 통과했다면 인증 통과라고 출력하지 않는다.

복구는 원인을 진단한 뒤 본 도구 소유 설정만 수정한다. 기존 계정 암호 재설정, host key 교체, 광범위 방화벽 개방을 만능 해결책으로 쓰지 않는다.

### FR-09. 제거와 되돌리기

기본 제거 대상은 도구가 만든 서비스/작업, 방화벽 규칙, 포워딩, 설치 파일이다. 기존 계정, 배포판, Linux 데이터, 다른 사람이 추가한 키는 보존한다.

새로 생성한 Windows 계정도 기본적으로 자동 삭제하지 않는다. 생성 이후의 데이터가 있을 수 있으므로 계정 삭제는 별도 검토·승인 항목이다.

복원 대상이 설치 이후 외부에서 변경됐다면 원본을 강제로 덮어쓰지 말고 충돌을 보고한다. 전역 기능인 WSL 자체의 제거는 일반 원복에 포함하지 않는다. 복구 가능 범위를 과장하지 않는다.

---

## 8. 권한 구조: 구현 전 작은 검증부터

### 8.1 반드시 지킬 경계

| 구성요소 | 실행 권한 | 할 수 있는 일 |
|---|---|---|
| 콘솔 UI·계획 엔진 | 일반 사용자 | 진단, 입력, 계획 제시, 상태 표시 |
| 설치 관리자 작업 | 사용자 승인 후 상승 권한 | 계정·RDP·방화벽·제품 설치·정책 변경 |
| 소유자 에이전트 | 대상 WSL 소유자 세션 | WSL 실행, Linux 검사, IP 보고, 수명 관리 |
| 제한된 네트워크 관리부 | 검증한 최소 관리 권한 | 승인된 포워딩 endpoint의 유지·갱신 |

**금지:** SYSTEM으로 `wsl.exe`를 호출하면 다른 사용자의 Ubuntu도 자동으로 보일 것이라는 설계. 일반 사용자에게 최고 권한 작업을 지정하면 관리자 권한이 생긴다는 설계. [S03][S05]

### 8.2 구현 기본안

초기 POC는 사용자 승인 기반 일회성 관리자 적용으로 연결을 확인한다. 자동 복구 제품화 때는 **소유자 에이전트 + 제한된 네트워크 관리부**를 사용한다.

관리부 구현 후보는 같은 배포 EXE의 내부 서비스 모드다. Windows 서비스가 먼저 떠 있어도 소유자 로그인 전 WSL을 실행하지 않는다. 제품의 부팅 무인 모드와 혼동하지 않는다. .NET은 Windows 서비스 구현과 단일 파일 배포를 지원한다. [S13]

일반 사용자 계정을 지원하기 어렵다는 이유로 모든 원격 사용자를 Administrators에 넣는 편법은 금지한다. 더 단순하고 안전한 구현이 검증되면 ADR로 제안할 수 있지만 계정·권한 경계를 유지해야 한다.

### 8.3 에이전트와 관리부 통신

로컬 전용 named pipe 같은 제한된 IPC를 검토한다. IPC에는 명시적인 ACL을 적용하고 호출 프로세스의 실제 SID를 확인한다. 기본 보안 설명자나 메시지에 적힌 SID만 신뢰하지 않는다. [S14]

요청은 `installationId`, `observedWslIPv4`, 상태 등 제한된 데이터만 담는다. 셸 명령, 임의 스크립트 경로, 임의 수신 주소·포트·파일 경로는 받지 않는다.

관리부는 관리자 승인으로 저장된 정책을 기준으로 소유자 SID, 대상 endpoint, 실제 WSL NAT 경로, 주소 범위, 등록한 SSH host key 정보를 검증한다. 일반 사용자가 쓸 수 있는 상태 JSON을 읽고 그대로 관리자 명령을 실행해서는 안 된다.

개발 1단계에서 다음 세 가지를 POC로 입증한다.

- 의도한 소유자 세션에서만 대상 배포판을 다룰 수 있다.
- 일반 계정 에이전트가 승인된 포워딩만 갱신할 수 있다.
- 다른 로컬 사용자, 변조된 IPC 요청, 임의 IP/포트는 거부된다.

안전한 자동 권한 분리가 검증되지 않았으면 자동 복구를 완료 표시하지 않는다. 우선 수동 승인형 POC로 제한하고 부족한 점을 기록한다.

---

## 9. 설치 상태와 재개 설계

### 9.1 상태 모델

```text
NotStarted → Diagnosing → Planned → AwaitingApproval → Applying
                                                   ├─ AwaitingReboot
                                                   ├─ AwaitingOwnerLogin
                                                   ├─ AwaitingUserInput
                                                   ├─ AwaitingAuthenticationSetup
                                                   ├─ Verifying → Completed
                                                   └─ Failed → RecoveryPlanned
```

실제 단계 순서에는 의존성이 있다. 상위 단계가 승인되지 않았거나 준비되지 않았으면 다음 단계가 적용되지 않아야 한다. 재개 시 저장된 완료 표시만 믿지 말고 실제 상태를 다시 비교한다.

### 9.2 상태 파일 필드

`schemaVersion`, `installationId`, `appVersion`, `ownerSid`, `distroName`, `approvedPlanHash`, `approvalHistory`, `stepStates`, `resourceOwnership`, `beforeSnapshots`, `verificationResults`, `pendingAction`, `lastError`, `updatedAt`.

비밀번호·토큰·개인키·전체 환경변수 덤프는 저장하지 않는다. 승인 이력은 누가 어떤 변경 범위에 동의했는지 기록하는 제품 내부 증적이지, 권한 통제를 대신하는 장치가 아니다.

파일은 잠금과 atomic replace를 사용해 기록한다. 중간 종료에도 이전 정상 파일을 읽을 수 있어야 한다. 버전 미지원·손상은 복구 진입 사유이며 초기화 후 진행하는 이유가 아니다.

### 9.3 리소스 소유권

- 설치별 GUID를 생성한다.
- 방화벽·작업·서비스는 도구 접두어와 ID를 사용한다.
- portproxy는 label이 없으므로 정확한 수신 tuple, 변경 전후, 소유권을 별도 기록한다.
- 파일과 Linux 설정은 만든 파일·추가한 키·편집한 영역을 기록한다.
- 정책과 실행 파일의 ACL, 경로, 버전을 검증한다.

### 9.4 실행 재개

재부팅 뒤 `resume`로 이어갈 수 있다. 같은 EXE를 다시 열어도 미완료 설치를 찾아 이어하기를 제안한다. 다른 Windows 계정에서 열리면 필요한 소유자 로그인 단계를 안내한다.

로그온 자동 작업이 재부팅 이후 필요한 사용자 입력까지 대신 완료한 것으로 기록하지 않는다. 저장한 비밀번호로 사용자 세션을 흉내 내지 않는다.

---

## 10. 단일 EXE와 설치 후 파일

**사용자에게 배포하는 파일은 하나지만, 설치 후 파일을 하나만 남기는 제품은 아니다.**

설치 경로 제안:

```text
C:\Program Files\WslRemoteSetup\
  WslRemoteSetup.exe              # 실행 파일. 일반 사용자 쓰기 금지

C:\ProgramData\WslRemoteSetup\
  policy\                        # 관리자 승인 정책. 일반 사용자 수정 금지
  state\                         # 작업 역할에 맞춘 ACL, 입력 신뢰 경계 구분
  logs\                          # 비밀 제외. 크기·보존 정책 적용
  backups\                       # 변경 전 데이터. 접근 제한

%LOCALAPPDATA%\WslRemoteSetup\
  user-state\                    # 소유자 에이전트용 비권한 상태
```

프로젝트 소스, Downloads, Temp에 있는 스크립트를 최고 권한 자동 작업의 실행 대상으로 직접 등록하지 않는다. 정책 수정은 관리자 승인 경로로만 가능해야 한다.

권장 publish 예시 — 프로젝트 생성 이후 Windows 빌드 환경에서 사용:

```powershell
dotnet publish .\src\WslRemoteSetup\WslRemoteSetup.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:PublishTrimmed=false `
  -p:DebugType=embedded `
  -o .\artifacts\publish\win-x64
```

단일 파일·self-contained 배포, 네이티브 라이브러리 포함과 추출 경로는 공식 문서를 기준으로 확인한다. 특히 상승 권한 프로세스의 추출 위치를 다른 권한의 사용자가 변조할 수 없도록 해야 한다. 해당 설정만 켜면 안전한 EXE가 완성되는 것은 아니다. [S15]

배포 전 `.NET SDK/런타임`, Python, Node.js가 없는 지원 Windows에서 실행을 확인한다. WSL·Ubuntu·Linux 패키지는 설치 시 공식 온라인 경로에서 받는다. 인터넷 연결 없는 환경에서는 지원 제한을 명확히 출력한다.

출력: `WslRemoteSetup.exe`, SHA-256, 버전·빌드 정보, 사용법, 알려진 제한, 실제 테스트 결과. 부가 문서와 해시는 배포 검증 자료이며 실행 필수 파일이 아니다. 코드 서명 여부를 정확히 표시하고, 백신·SmartScreen 전체 해제를 설치 방법으로 권하지 않는다.

---

## 11. 코드 구조와 구현 계약

초기 폴더 제안이며 의미 없이 프로젝트 수를 늘리지 않는다.

```text
wsl-remote-setup/
├─ START_HERE.md
├─ CLAUDE.md
├─ docs/
│  ├─ DEVELOPMENT_PLAN.md
│  ├─ IMPLEMENTATION_STATUS.md
│  ├─ TEST_RESULTS.md
│  └─ adr/
├─ src/
│  ├─ WslRemoteSetup/             # 콘솔, 내부 에이전트/관리 모드
│  └─ WslRemoteSetup.Core/        # 계획·상태·검증 정책
├─ tests/
│  ├─ WslRemoteSetup.UnitTests/
│  └─ WslRemoteSetup.IntegrationTests/
├─ scripts/                      # 빌드·테스트용. privileged runtime과 분리
├─ resources/                    # 검증된 내장 스크립트/템플릿
├─ .local/                       # 현장 환경 보고·개발 로그. Git 제외
├─ artifacts/                    # 빌드 산출물. Git 제외
├─ global.json
├─ .gitignore
└─ .gitattributes
```

도메인 모델: `EnvironmentSnapshot`, `DesiredConfiguration`, `InstallationPlan`, `PlanStep`, `ApprovalRecord`, `OwnedResource`, `VerificationResult`, `InstallState`.

단계별 계약:

```text
Probe → Plan → Validate → RequireApproval → Apply → Verify
                                                   └→ Recover/Rollback
```

외부 명령은 공통 실행 계층으로 모은다. 실행 파일 allowlist, 인수 배열, timeout, 취소, 종료코드, 표준출력·오류 처리, 마스킹을 담당한다. 명령 성공을 상태 성공과 동일시하지 않는다.

Windows 계정명, Linux 계정명, 배포판 식별자, 파일 경로, 포트, IP/CIDR은 각각 검증한다. `linux_admin`의 underscore를 허용하는지 테스트한다. 비밀번호의 특수문자를 셸 quoting으로 처리하려 하지 말고 전용 입력 경로를 쓴다.

### 11.1 CLI 제안

```text
WslRemoteSetup.exe                    # 대화형 마법사
WslRemoteSetup.exe doctor             # 비파괴 진단
WslRemoteSetup.exe plan               # 현재 상태 기반 계획
WslRemoteSetup.exe install            # 승인 기반 적용
WslRemoteSetup.exe resume             # 중단 지점 재개
WslRemoteSetup.exe status             # 현재 상태
WslRemoteSetup.exe repair             # 진단 후 승인된 복구
WslRemoteSetup.exe pause              # 자동 복구 중지
WslRemoteSetup.exe resume-monitoring  # 자동 복구 재개
WslRemoteSetup.exe uninstall          # 소유 리소스 제거 계획·승인
```

이 문서의 CLI는 구현 목표다. 현재 실행 가능한 프로그램이 존재하는 것으로 설명하지 않는다. 임의 권한 작업을 받는 범용 `--run-command`나 위험 변경을 모두 승인하는 `--yes`는 제공하지 않는다.

---

## 12. 개발 단계와 완료 기준

| 단계 | 구현·작업 | 완료 기준 |
|---|---|---|
| M0 | G2 초기 진단, 현황 기록, 변경 계획 | 실제값/예상값 구분, 기존 IP·RDP 보존 계획 작성 |
| M1 | 승인 기반 G2 WSL·Ubuntu 준비, 개발 도구 | 소유자 SID·Ubuntu·WSL 2·기본 계정 검증 |
| M2 | 솔루션, doctor, plan, 입력 검증, 상태 저장 | 시스템 변경 없이 진단·계획·단위 테스트 통과 |
| M3 | 소유자/관리 권한 분리 POC, 수명 관리 POC | 일반 계정 포함 권한 경계와 로그인 동작 입증 |
| M4 | SSH·Windows 계정·RDP·방화벽·포워딩 적용 | 승인된 변경만 수행, 로컬 경로 검증 |
| M5 | 자동 실행, 복구, 재개, pause, 제거 | 중복·재시작·오류·원복 시나리오 통과 |
| M6 | G2 LAN 클라이언트 통합 테스트 | 실제 다른 PC의 SSH/RDP 인증 결과 기록 |
| M7 | 단일 EXE, 깨끗한 Windows 신규 설치 테스트 | 개발 도구 없는 지원 환경에서 배포 흐름 검증 |

M0~M2를 먼저 구현하고 작은 결과를 보여준다. 처음부터 GUI·VPN·DDNS·웹 대시보드를 확장하지 않는다. M3의 핵심 권한·수명 가정은 전체 코드 작성 전에 검증한다.

**G2에 부트스트랩을 수행한 뒤에는 G2가 더 이상 “WSL 미설치 신규 PC”가 아니다.** 이후 깨끗한 설치 테스트는 별도 PC 또는 WSL 2를 구동할 수 있는 검증된 Windows VM/스냅샷 환경에서 한다. G2의 배포판을 삭제해 반복 시험하지 않는다. 그러한 환경이 없으면 M7은 `NOT_TESTED`로 남기고 그 사실을 릴리스 노트에 적는다.

---

## 13. 검증 시나리오

아래 테스트는 처음에는 미수행이다. Claude는 실제 수행 결과와 로그를 남겨 업데이트한다.

| ID | 시나리오 | 기대 결과 |
|---|---|---|
| T01 | WSL 없는 지원 Windows | 진단·승인·재부팅·소유자 초기화·재개 동작 |
| T02 | 현재 관리자와 WSL 소유자가 다름 | 잘못된 계정에 배포판을 만들지 않음 |
| T03 | 기존 `owneradmin`·RDP 포트 존재 | 기존값 보존, 중복 계정·규칙 없음 |
| T04 | TCP 1004가 다른 프로세스/규칙과 충돌 | 원인을 표시하고 기존 리소스 미변경 |
| T05 | 계정 비밀번호·Linux 입력에 특수문자 | 오류 없는 처리, 로그·인수 노출 없음 |
| T06 | 한글 Windows 사용자·경로 | 진단·설치·로그·재개 정상 |
| T07 | 공개키 중복·기존 authorized_keys | 기존 키 보존, 중복 추가 없음 |
| T08 | SSH 설정 구문 또는 유효값 오류 | 서비스 반영 전 차단·복구 가능 |
| T09 | 일반 사용자 소유자 + 관리자 설정 계정 | 승인된 포워딩만 자동 갱신 |
| T10 | 다른 로컬 사용자의 IPC·설정 변조 | 거부·기록, 관리자 코드 실행 없음 |
| T11 | WSL IP 변경 | Windows 수신 주소 유지, 대상만 갱신 |
| T12 | 네트워크가 다른 어댑터/공용망으로 바뀜 | 노출 범위 자동 확대 없음 |
| T13 | Wi-Fi 끊김·복구 | backoff, 복구 후 승인된 경로만 회복 |
| T14 | RDP 창 닫기·잠금 | 세션 유지 여부와 실제 SSH 가용성 기록 |
| T15 | 로그아웃·재부팅·다시 로그인 | 1.0 한계 표시, 로그인 후 자동 회복 |
| T16 | pause 후 WSL 종료 | 자동 재시작하지 않음 |
| T17 | 정상 상태에서 설치·repair 재실행 | 계정·규칙·작업 중복 없음 |
| T18 | 설치 중 프로세스 종료·상태 파일 손상 | 검증 후 재개 또는 안전한 복구 안내 |
| T19 | 다운로드·패키지 설치·권한 실패 | 실패 단계 보존, 다른 작업 무단 진행 없음 |
| T20 | G2 자체 TCP 검사만 성공 | 외부 인증은 NOT_TESTED 유지 |
| T21 | 다른 LAN PC에서 실제 SSH 인증 | 올바른 계정·host key·연결 경로 확인 |
| T22 | 다른 LAN PC에서 실제 RDP 인증 | 기존 포트·허용 사용자·NLA 유지 확인 |
| T23 | 도구 제거 후 기존 서비스 접속 | 기존 리소스 보존, 도구 리소스만 제거 |
| T24 | SDK·Python·Node 없는 깨끗한 Windows | EXE 기동 및 지원 설치 흐름 정상 |
| T25 | Windows Home | RDP 우회 없이 SSH-only로 안내 |
| T26 | 광범위 기존 방화벽 규칙 존재 | 제한 완료로 오인하지 않고 경고 |

파괴적·네트워크 단절 테스트는 별도 승인과 복구 수단이 있는 환경에서만 수행한다. G2에서 T13·T15를 무조건 실행하지 않는다.

보고 형식: 테스트 ID, 환경/버전, 실행 주체, 실행 시각, 기대값, 실제값, PASS/FAIL/NOT_TESTED, 관련 로그, 잔여 작업. 테스트를 위한 임시 비밀번호·키를 결과 파일에 쓰지 않는다.

---

## 14. 실패와 운영 정책

오류 메시지는 “무엇이 실패했는가 / 현재 무엇은 유지되는가 / 다음에 무엇을 해야 하는가”를 포함한다.

예:

```text
[PORT_CONFLICT] Windows TCP 1004를 다른 프로세스가 사용하고 있습니다.
기존 포워딩과 방화벽은 변경하지 않았습니다.
다른 포트를 선택하거나 해당 서비스의 사용 여부를 확인해 주세요.
```

```text
[OWNER_LOGIN_REQUIRED] 대상 Ubuntu는 선택한 Windows 계정에서 초기화해야 합니다.
Windows WSL 기능 설치는 완료했으며 Linux 배포판 초기화는 대기 중입니다.
해당 계정으로 로그인한 뒤 같은 EXE의 '이어하기'를 실행해 주세요.
```

재시도는 횟수·간격·취소 조건을 둔다. 설치 도중의 사용 취소를 실패와 구분한다. 실행 중 작업에 timeout을 두고 프로세스가 종료되지 않으면 원인을 기록한다.

로그는 로컬 저장을 기본으로 하고 원격 텔레메트리는 기본 비활성이다. 사용자가 로그를 내보내기 전에 개인정보·접속정보 포함 범위를 안내하고 비밀값을 제거한다.

---

## 15. Claude 작업 방식과 첫 응답

### 15.1 지침

- 설명만 반복하지 말고, 현재 단계에서 가능한 진단·파일 작성·구현·검증을 수행한다.
- 읽기/코드 작성/단위 테스트를 매번 재승인받지 않는다.
- 시스템 변경·인증·계정 전환·재부팅 경계에서만 구체적인 승인을 요청한다.
- 승인받은 단계가 끝나면 결과를 검증하고 완료 범위를 보고한다.
- 긴 작업은 단계별로 나눠 `docs/IMPLEMENTATION_STATUS.md`에 다음 작업을 남긴다.
- 완료·실패·미수행을 구분한다. 실제 접속하지 않은 원격 검증을 만들지 않는다.
- 공식 문서의 최신 명령과 현장 버전의 지원 여부가 다르면 차이를 기록한다.
- 문서의 안전 요구는 제품 코드·ACL·OS 권한으로 구현한다. `CLAUDE.md`만으로 강제된다고 생각하지 않는다. [S16]

### 15.2 첫 세션의 구체적 목표

1. 루트 `CLAUDE.md`, 본 문서, 기존 저장소 지침을 읽는다.
2. `.local/` 제외 설정과 진단 보고 틀을 만든다.
3. G2 읽기 전용 진단을 수행하고 실측값을 기록한다.
4. WSL 소유자 후보와 기존 원격접속 보존 계획을 제시한다.
5. WSL 런타임 설치부터 필요한 변경 단계를 정리한다.
6. 첫 OS 변경 직전에 해당 단계의 승인만 요청한다.

첫 응답에서 전체 시스템을 한꺼번에 설치하지 않는다. 반대로 “어떤 걸 만들까요?”처럼 이미 문서에 있는 요구사항을 다시 묻지도 않는다.

보고 예:

```text
진단 완료: Windows ..., 계정 ..., 현재 IP ..., RDP 포트 ...
WSL/Ubuntu: 미설치 확인 / 조회 제한 / 실제 상태
보존할 항목: 현재 IP, DNS, RDP 포트, 기존 계정
제안 소유자: ... (확인 근거)
다음 변경: WSL 런타임 설치. Ubuntu 등록은 소유자 계정에서 별도 진행.
승인 필요: Windows 기능 설치 및 재부팅 가능성.
```

---

## 16. 근거 문서

확인일: 2026-10-17. 아래는 플랫폼 동작과 설치 옵션의 근거다. 본 문서의 제품 구조·단계·기본값·범위는 해당 근거를 바탕으로 한 **이번 프로젝트의 설계 결정**이다. 웹 문서와 현장 버전이 다르면 실제 지원 여부를 다시 확인한다.

- **[S01] Anthropic — Advanced setup / Native Windows, installation, verification**  
  https://code.claude.com/docs/en/setup
- **[S02] Microsoft — Basic commands for WSL**  
  https://learn.microsoft.com/en-us/windows/wsl/basic-commands
- **[S03] Microsoft — Set up a WSL development environment / per-user distributions**  
  https://learn.microsoft.com/en-us/windows/wsl/setup/environment
- **[S04] Microsoft — Accessing network applications with WSL**  
  https://learn.microsoft.com/en-us/windows/wsl/networking
- **[S05] Microsoft — Security Contexts for Tasks**  
  https://learn.microsoft.com/en-us/windows/win32/taskschd/security-contexts-for-running-tasks
- **[S06] Microsoft — Enable Remote Desktop on your PC**  
  https://learn.microsoft.com/en-us/windows-server/remote/remote-desktop-services/remotepc/remote-desktop-allow-access
- **[S07] Microsoft — netsh interface / portproxy**  
  https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/netsh-interface
- **[S08] Canonical — OpenSSH server**  
  https://ubuntu.com/server/docs/how-to/security/openssh-server/
- **[S09] Microsoft — Use systemd to manage Linux services with WSL**  
  https://learn.microsoft.com/en-us/windows/wsl/systemd
- **[S10] Microsoft — Systemd support is now available in WSL**  
  https://devblogs.microsoft.com/commandline/systemd-support-is-now-available-in-wsl/
- **[S11] Microsoft — .NET and .NET Core Support Policy**  
  https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core
- **[S12] Microsoft — Install .NET on Windows**  
  https://learn.microsoft.com/en-us/dotnet/core/install/windows
- **[S13] Microsoft — Create Windows Service using BackgroundService**  
  https://learn.microsoft.com/en-us/dotnet/core/extensions/windows-service
- **[S14] Microsoft — Named Pipe Security and Access Rights**  
  https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights
- **[S15] Microsoft — Create a single file for application deployment**  
  https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview
- **[S16] Anthropic — How Claude remembers your project / CLAUDE.md**  
  https://code.claude.com/docs/en/memory

---

## 17. 한 문장으로 정리

**G2의 현재 Windows 환경과 원격접속을 지키면서 Ubuntu WSL부터 준비하고, 그 과정을 다른 PC에서도 승인·재개·복구 가능하게 반복할 수 있는 Windows 단일 EXE 제품으로 구현한다.**
