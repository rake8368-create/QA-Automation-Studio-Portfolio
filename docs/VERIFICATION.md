# 공개 Windows 검증 보고서

공개 워크북과 import 설정을 사용하는 .NET 10 Windows 검증입니다. 제품의 실행형 하니스와 게시 EXE는 각각 종료 코드와 결과 증거를 남깁니다. [워크플로](../.github/workflows/windows-qa.yml) · [검증 도구](../tools/verify-public.ps1)

## 공개 CI 실행 범위

| 게이트 | 입력과 확인 결과 |
| --- | --- |
| 솔루션 Release | -warnaserror, 경고 0·오류 0 |
| 기본 실행형 하니스 | 74/74 PASS, 실패 0. 공개 워크북 70건은 NOT RUN이며, 별도 합성 DB에서 Windows 프로세스·창 경계, 매핑·텍스트 오케스트레이션 및 SQLite 원자성을 확인 |
| 결과 패키지 모드 | --result-package 프로세스 종료 0, 합성 패키지 2개와 패키지당 Run별 증적 인덱스 6개. 누락·해시 불일치와 실패 시 롤백을 확인 |
| 게시 Windows x64 EXE | self-contained 단일 EXE. --verify-import 프로세스 종료 0 및 acceptance.json 10/10 PASS. 합성 XLSX의 필수값·중복·별칭·재시작·롤백 검증 |
| SQLite와 공개 해시 | 합성 DB schema v6, integrity_check 및 foreign_key_check 통과. 공개 매니페스트, 기존 F3 비식별 사본 SHA-256 5건 대조 |

실패, 누락, 결과 수 불일치 또는 종료 코드 비정상은 CI 실패입니다. 공개 아티팩트에는 요약 JSON의 수치와 검증 상태만 포함됩니다. 원시 JSON, SQLite, XLSX, 로컬 경로 및 운영 자료는 로컬 검증 작업 공간에서만 사용합니다. [결과·무결성 검증 코드](../tools/verify-public.py)

## 모드별 자료

| 하니스 | 필수 자료 | 이번 공개 CI |
| --- | --- | --- |
| 기본 인자 | 공개 XLSX, import JSON, 세 네이티브 점검용 임시 SQLite | 실행. 공개 소스에서 동일 계약의 격리 DB를 준비 |
| --result-package | 빈 출력 경로 | 실행. 하니스가 자체 DB와 합성 증적 생성 |
| 게시 EXE --verify-import | 70건 시작 DB, 9개 시험 워크북, 롤백 조건 | 실행. 공개 샘플에서 별도 합성 |
| --day7 · --day8 | 각 v2/v3 시점 DB, 고정 매핑 수와 행·스키마 검증 | 미실행. 해당 역사 DB는 공개본 범위 밖 |
| --day9 · --day11 | 각 시점 운영 복제본의 행·Run·매핑 및 v4 스냅샷 조건 | 미실행. 해당 역사 DB는 공개본 범위 밖 |
| --manual-sprint | v5 76건·17 Run의 역사 스냅샷과 수동 데이터 계약 | 미실행. 수동 GUI 근거는 기존 격리 검증 기록 사용 |

[Day7Checks](../tests/QAStudio.Tests/Day7Checks.cs) · [Day8Checks](../tests/QAStudio.Tests/Day8Checks.cs) · [Day9Checks](../tests/QAStudio.Tests/Day9Checks.cs) · [Day11Checks](../tests/QAStudio.Tests/Day11Checks.cs) · [ManualSprintChecks](../tests/QAStudio.Tests/ManualSprintChecks.cs)

## 기록과 범위

- 공개 제품 소스 기준은 `215592e3766146c3ac95303af7ac8ca741e17ff1`입니다. 원본과 비교한 소스·테스트·빌드 설정 47개 중 46개가 일치합니다. 공개용 NotepadTextProbe는 PC 고정 경로 대신 `QASTUDIO_MACROSTUDIO_QA_EXE`로 지정된 QA EXE를 검증합니다. 이 공개용 차이를 유지했습니다. [대상 경로 확인](../src/QA.AutomationStudio.Desktop/NotepadTextProbe.cs#L298)
- 최초 공개 Windows CI는 `81f051046b32448625c4e8f2261877ccc0e15d22`에서 위 게이트를 통과했습니다. 최신 문서·PDF를 포함한 main은 실행별 SHA로 구분합니다. [최초 제품 검증 CI](https://github.com/rake8368-create/QA-Automation-Studio-Portfolio/actions/runs/45518035153) · [최신 main의 Windows QA 실행 목록](https://github.com/rake8368-create/QA-Automation-Studio-Portfolio/actions/workflows/windows-qa.yml?query=branch%3Amain)
- 게시 스모크는 가져오기 검증 모드입니다. 실제 F3는 CI에서 0건이며, [2026년 11월 5~10일의 실제 F3 5건](../reports/submission-evidence/INDEX.md)은 기존 로컬 증적입니다. 별도 SUT·대화형 세션과 원본 운영 DB를 CI에 제공하지 않습니다.
- 운영 집계와 공개 CI는 [실행 결과](EXECUTION_RESULTS.md)에 별도로 기록했습니다. 제품 현장 실행과 코드 커버리지는 측정 범위 밖입니다.

## 현재 공개 빌드의 격리 GUI 증거

2026-11-25에는 공개 소스로 게시한 EXE를 시험용 SQLite와 함께 열었습니다. 합성 Excel에서 열 매핑 및 중복 1건의 기본 차단을 확인하고 취소했습니다. 매핑 목록과 수동 결과·이력 양식을 열고, 격리 결과 패키지를 실제 내보냈습니다. 결과 Excel을 열어 70개 TC의 NOT RUN과 비어 있는 Run ID를 확인했습니다. 기존 격리 수동 PASS 3건은 별도 검증 기록입니다. [중복 차단](../reports/screenshots/Sprint41_Import.jpg) · [수동 화면](../reports/screenshots/Sprint41_Manual.jpg) · [결과 Excel](../reports/screenshots/Sprint41_Excel.jpg)

## 로컬 재현

Windows x64에서 .NET 10, PowerShell 7, Python 3.13을 사용할 수 있습니다. 저장소 루트에서 새 출력 폴더를 지정합니다.

```powershell
python tools/verify-public.py manifest
./tools/verify-public.ps1 -OutputDirectory ./work/public-qa
```
