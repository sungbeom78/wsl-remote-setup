# ADR 0002 — 솔루션 구조와 읽기 전용 진단 방식

- 상태: 채택
- 날짜: 2026-10-18 (M2)

## 결정

1. **프로젝트 구분**
   - `WslRemoteSetup.Core` (`net10.0`): 순수 정책 라이브러리. 입력 검증, CIDR 계산, WSL 주소 선택, Doctor 평가, Planner, 상태 저장, 명령 실행 계층을 담는다. Windows API에 의존하지 않으므로 시스템을 변경하지 않고 단위 테스트할 수 있다.
   - `WslRemoteSetup` (`net10.0-windows`, `win-x64`): 콘솔 진입점과 `WindowsProbe`.
   - `tests/WslRemoteSetup.UnitTests` (xUnit): 시스템을 변경하지 않는 테스트만 둔다. 시스템을 변경하는 통합 테스트는 이후 별도 프로젝트로 분리하고, 실행하려면 명시적 허용 조건이 필요하게 한다.
2. **진단은 현지화된 텍스트를 파싱하지 않는다.** G2의 `wsl.exe` 출력은 한국어이고 UTF-16LE다. 그래서 다음 구조화된 소스를 사용한다.
   - 레지스트리: RDP, 재부팅 대기, portproxy, 현재 사용자의 Lxss
   - WMI: 선택 기능, 계정, 그룹 구성원(SID 기준), 네트워크 프로필
   - .NET NetworkInformation: 어댑터, IPv4, DHCP, 게이트웨이, DNS, 리스너
   - 방화벽 COM(HNetCfg.FwPolicy2)
   - `wsl --version`은 종료코드와 숫자 패턴만 사용한다.
3. **doctor는 배포판을 기동하지 않는다.** Lxss 레지스트리와 `wsl --version`만 조회한다. 실기 검증에서 doctor 실행 후에도 Ubuntu-24.04가 `Stopped` 상태로 유지되는 것을 확인했다. 그 결과 SSH 상태는 plan에서 "확인 필요"로 표시된다.
4. **명령 실행 계층**은 다음 규칙을 따른다.
   - 절대 경로 allowlist(`System32\wsl.exe`, `netsh.exe`)에 있는 실행 파일만 허용한다.
   - 인수는 `ArgumentList` 배열로만 전달하고 셸을 거치지 않는다.
   - stdin을 즉시 닫는다.
   - timeout과 취소 시 프로세스 트리를 종료한다.
   - UTF-16LE/UTF-8을 판별하고 비밀값을 마스킹한다.
5. **WSL 주소 선택**은 `ip -j` JSON에서 기본 경로 인터페이스를 찾아 결정한다. `lo`는 항상 제외한다(G2 실측: `lo`에 `10.255.255.254/32 scope global`이 있음).
6. **방화벽 평가**는 프로그램·서비스·앱 패키지에 묶인 와일드카드 규칙을 서비스 포트 허용으로 세지 않는다. 프로그램 제한 없이 모든 포트를 여는 규칙은 별도로 경고한다. 이는 G2 실기 doctor에서 발견한 오탐을 수정한 결과다.

## 대안

- **PowerShell cmdlet 호출 후 텍스트 파싱**: PS 5.1 인코딩·quoting 문제와 현지화 문제가 있어 진단 경로에서 배제했다.
- **WSL 내부 검사(`wsl -e`)를 doctor에 포함**: FR-01("중지된 배포판을 몰래 깨우지 않는다")에 따라 배제했다. 이후 승인된 verify 단계에서 수행한다.

## 제한

- 방화벽 COM의 `Name`은 표시 이름이다(규칙 고유 ID가 아님). 이 도구가 만드는 규칙은 M4에서 고유 이름 접두어로 소유권을 관리한다.
- `OutputDecoder`의 UTF-16 판별은 휴리스틱이다. 공백이나 ASCII 없이 한글만으로 이루어진 출력은 오판할 수 있다.
- 패키지 의존성은 `System.Management`(Microsoft) 하나다.
