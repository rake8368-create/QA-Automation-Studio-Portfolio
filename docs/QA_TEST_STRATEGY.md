# QA 테스트 전략

개인 프로젝트에서 Codex와 구현 및 검증을 반복했습니다. 요구사항과 합격 조건을 TC에 기록하고, 가져오기·수동 판정·자동 실행·증적 내보내기의 단언을 각각 설계했습니다. 설계 명세, 제품 자체 회귀 그리고 별도 SUT의 실제 실행은 증거와 집계를 구분합니다.

## 설계 기법

| 기법 | 대표 TC | 확인 조건 | 연결 근거 |
| --- | --- | --- | --- |
| 경계값 분석 | PRO-002 | 프로파일 이름 1·50자는 허용, 51자는 거부 | [설계 샘플](../test-data/QA_Automation_Portfolio_TC_70.xlsx) · [경계 명세 보존 검사](../tests/QAStudio.Tests/Program.cs#L92) |
| 결정 테이블 | MAC-003 | 매크로 OFF에서 F3가 들어와도 매크로가 출력 키를 보내지 않아야 함 | [OFF·출력 관찰 단언](../tests/QAStudio.Tests/Day7Checks.cs#L34) · [별도 실제 TC MAC-QA-003](../reports/submission-evidence/MAC-QA-003.redacted.json) |
| 상태 전이 | MAC-001 | 초기 OFF, 단축키 감시 비활성, 입력 없음 | [설계 샘플](../test-data/QA_Automation_Portfolio_TC_70.xlsx) · [별도 제품 자동화 정의 전이 검사](../tests/QAStudio.Tests/Program.cs#L235) |
| 동등 분할 | AUD-002 | 사용자 정의 분·초 치환값 검토. 설계의 대표 유효값은 3분 59초 | [검증 조건](TEST_DESIGN.md) · [TC 17개 원본 필드 왕복](../tests/QAStudio.Tests/Program.cs#L115) |

PRO-002, MAC-001, MAC-003, AUD-002는 설계 사례입니다. 위의 하니스 소스는 관련 설계 보존이나 제품의 별도 계약을 검사하며, 해당 SUT TC를 이번 CI에서 직접 실행한 기록은 없습니다.

| 제품 회귀 범위 | 기대 및 판정 | 테스트 |
| --- | --- | --- |
| Excel 입력 | 필수 제목·값과 우선순위를 검증. 명시적 별칭 허용. 파일·같은 프로젝트 중복은 기본 차단 | [워크북 하니스](../tests/QAStudio.Tests/Program.cs#L153) · [게시 EXE 가져오기 검증](../src/QA.AutomationStudio.Desktop/ImportVerification.cs) |
| 실행 사전조건 | 등록된 TC·대상·문서·읽기 조건을 확인. 사전조건 실패나 미관찰 상태를 PASS로 기록하지 않음 | [실행기 텍스트 단언](../tests/QAStudio.Tests/SingleTextChecks.cs) · [PID·시작 시각·실행 파일 식별](../tests/QAStudio.Tests/Program.cs#L402) |
| 판정 | PASS·FAIL에는 관찰과 단언이 필요. ERROR는 실행·관찰 오류, BLOCKED는 실행 차단, CANCELLED는 취소, NOT RUN은 이력 없음 | [저장 판정 제약](../tests/QAStudio.Tests/Program.cs#L243) · [결과 Excel의 상태 어휘](../tests/QAStudio.Tests/ResultPackageChecks.cs#L87) |
| SQLite | 프로젝트 범위 참조, Run 불변성, Run·증적의 원자적 기록, 마이그레이션 후 보존 | [저장 롤백](../tests/QAStudio.Tests/Program.cs#L385) · [매핑 v1 마이그레이션](../tests/QAStudio.Tests/MappingChecks.cs#L44) |
| 증적과 결과 Excel | 해시 확인, 누락·불일치 표시, 정확한 최신 Run과 이력, 실패한 내보내기 미게시 | [패키지 회귀](../tests/QAStudio.Tests/ResultPackageChecks.cs#L87) |
| 자동·수동 | 명시적 매핑 저장·재시작·변경·해제. 수동과 자동 이력은 별도이며 최신 판정을 시간 기준으로 표시 | [자동 매핑 회귀](../tests/QAStudio.Tests/MappingChecks.cs#L66) · [별도 v5 스냅샷을 사용하는 수동 회귀](../tests/QAStudio.Tests/ManualSprintChecks.cs) · [격리 GUI 결과](MANUAL_READINESS.md) |
| Windows 배포 | .NET 10 Release 오류·경고 0, 하니스·패키지 종료 코드, 결과 JSON, 단일 EXE 자체의 가져오기 스모크 | [워크플로](../.github/workflows/windows-qa.yml) · [실행 절차와 게이트](VERIFICATION.md) |

코드와 Excel 워크북 검증은 제품 자체 점검입니다. CI에서 실제 F3는 보내지 않았으며 코드 커버리지나 전체 TC 실행률은 측정하지 않았습니다. [실행 집계](EXECUTION_RESULTS.md)
