# G2에서 개발 시작하기

이 묶음은 **기획 문서와 Claude 작업 지침**이다. 실행 가능한 제품 EXE나 G2 검사 결과가 들어 있는 것은 아니다.

## 1. 파일 배치

새 프로젝트 경로 제안은 `C:\projects\wsl-remote-setup`이다. 압축을 풀었을 때 다음과 같이 두면 된다.

```text
C:\projects\wsl-remote-setup\
  START_HERE.md
  CLAUDE.md
  docs\
    DEVELOPMENT_PLAN.md
```

기존 폴더가 있으면 기존 파일을 덮어쓰지 않는다. 프로젝트 경로가 다른 경우 그 위치에서 작업하면 된다.

| 파일 | 용도 |
|---|---|
| `docs/DEVELOPMENT_PLAN.md` | G2 초기 설정, 제품 요구사항, 기본값, 권한 구조, 단계, 테스트 기준 |
| `CLAUDE.md` | Claude가 프로젝트에서 지켜야 할 작업 규칙과 금지 사항 |
| `START_HERE.md` | 이 안내와 첫 입력 프롬프트 |

## 2. Claude Code를 Windows에 설치

**Ubuntu WSL을 먼저 설치할 필요는 없다.** Windows 네이티브 Claude Code를 설치해 G2 진단부터 시작한다. 일반 Windows PowerShell에서 공식 설치 명령을 실행한다. 이미 설치했으면 이 단계를 건너뛴다. [A]

```powershell
irm https://claude.ai/install.ps1 | iex
```

위 명령은 Anthropic의 공식 설치 스크립트를 다운로드해 실행하는 명령이다. 회사 정책 등으로 스크립트 실행이 제한되면 실행 정책을 전역 해제하지 말고 공식 문서의 다른 설치 경로를 사용한다.

설치 후 새 PowerShell 창을 열어 확인한다.

```powershell
claude --version
```

프로젝트 폴더에서 실행한다.

```powershell
Set-Location 'C:\projects\wsl-remote-setup'
claude
```

첫 실행의 인증은 사용자가 직접 진행한다. `claude` 명령을 찾지 못하면 새 터미널과 설치 PATH를 확인한다. Git for Windows는 개발 중 사용할 것이지만, 설치 여부 확인과 준비는 Claude의 G2 진단 단계에서 함께 진행해도 된다. [A]

### Claude를 이미 WSL에 설치한 경우

Windows 쪽 Claude를 주 작업 환경으로 사용한다. WSL의 Claude에서 진행하더라도 Windows 계정·UAC·실행 주체를 명시적으로 구분해야 한다. Linux에서 명령을 실행했다는 것만으로 Windows 설정이 검증된 것으로 처리하지 않는다.

## 3. Claude에게 처음 보낼 내용

아래 내용을 프로젝트 폴더에서 실행한 Claude Code에 붙여넣는다.

```text
이 프로젝트는 G2에서 개발한다.
G2에는 현재 Windows만 설치되어 있고 Ubuntu WSL 설치부터 필요하다.

먼저 CLAUDE.md와 docs/DEVELOPMENT_PLAN.md를 읽고 작업을 시작해 줘.
단순히 문서를 요약하거나 설명만 하지 말고, 실제 진단과 프로젝트 현황 파일 작성을 진행해.

첫 목표는 다음과 같다.
1. 현재 Windows 버전·권한·계정·네트워크·RDP·WSL·개발 도구를 읽기 전용으로 진단한다.
2. 이전 기록의 IP는 192.168.1.222이고 OS에서 직접 고정한 값이므로,
   실제값을 확인하고 현재 IP·DNS·게이트웨이·RDP 포트를 보존한다.
3. owneradmin 계정이 실제로 있는지 확인하고, 대상 WSL의 소유 Windows 계정을
   배포판 설치 전에 결정한다. 기존 계정을 쓸 수 있으면 새 계정을 불필요하게 만들지 않는다.
4. .local/을 Git에서 제외하고 현장 진단 보고서와 docs/IMPLEMENTATION_STATUS.md를 작성한다.
5. Ubuntu WSL부터 개발 환경을 준비할 단계와 변경 영향을 정리한다.
6. 읽기 전용 진단과 프로젝트 파일 작성은 바로 진행하되,
   계정·WSL 기능·RDP·방화벽·네트워크 변경과 재부팅은 해당 단계 직전에 승인받는다.

최종 제품은 Windows에서 실행할 단일 EXE다.
일반 설정은 기본값으로 진행하고, 비밀번호·위험한 변경은 별도 입력과 승인을 받게 한다.

개발은 C#/.NET 10 LTS와 Windows 네이티브 환경을 기준으로 하고,
콘솔 마법사, 상태 진단, 설치 계획, 재개, 연결 복구, 제거를 단계적으로 구현한다.
개발용 Claude/Git/.NET SDK를 배포받는 사용자의 필수 환경으로 만들지 않는다.

현재 실제로 확인한 값, 문서의 예상값, 미검증 항목을 구분해서 보고해 줘.
첫 OS 변경 직전에 필요한 승인만 요청하고, 이미 문서에 있는 요구사항은 다시 묻지 마.
```

## 4. 기대하는 진행 흐름

```text
Windows Claude Code
  → G2 실제 환경 진단
  → WSL 소유 Windows 계정 결정
  → 승인 후 WSL 런타임 설치
  → 필요한 경우 재부팅·재접속
  → 소유자 계정에서 Ubuntu 초기화
  → Git·.NET 개발 환경 준비
  → 진단/계획/테스트부터 제품 구현
  → SSH/RDP·포워딩·자동복구 구현과 검증
  → 단일 EXE 및 별도 신규 설치 검증
```

초기에는 운영체제의 재부팅, 계정 로그인, 비밀번호 입력처럼 사용자가 직접 해야 하는 단계가 있다. 이 경계에서 멈추는 것은 정상이다. 그 외에 문서에 정해진 개발 작업을 계속 다시 설명하고 승인받게 만들지는 않는다.

## 5. 중요한 한계

1. 이 문서 작성 과정에서 G2에는 접속하지 않았으며 어떠한 설정도 적용하지 않았다.
2. 1.0 기본은 **로그인 이후 LAN 접속**이다. 로그인 전 무인 실행과 인터넷 접속은 별도 범위다.
3. G2에 WSL을 준비한 다음에는 새 PC 설치 테스트를 위해 G2의 배포판을 삭제하지 않는다.

## 공식 문서

- [A] Anthropic, Claude Code 설치·Windows 네이티브 실행·검증: https://code.claude.com/docs/en/setup
- 전체 플랫폼 근거는 `docs/DEVELOPMENT_PLAN.md`의 16절에 수록했다.
