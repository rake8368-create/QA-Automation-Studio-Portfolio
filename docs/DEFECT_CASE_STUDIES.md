# 결함과 실행 오류를 분석한 사례

판정은 기록 당시 원인과 관찰을 기준으로 보존합니다. 운영 ERROR 7건은 5건의 시작 Smoke Test 오류와 2건의 MAC-QA-002 사전조건 오류입니다. 이후 PASS는 별도 Run입니다.

## 실제 제품 결함 — Sprint 4.1 Excel의 실행 가능 표시

- **재현과 기대** — 명시적 매핑 없는 기본 Smoke Test TC를 내보냅니다. 제품의 내장 실행기가 있으므로 TC_Master, TC_Results, Automation_Review에서 실행 가능 여부를 ‘예’로 표시해야 합니다.
- **관찰과 원인** — 이전 내보내기의 표시 기준이 명시적 매핑에만 의존해 내장 실행 경로가 있는 TC도 ‘아니요’로 표시했습니다.
- **수정과 재검증** — Sprint 4.1에서 기본 Smoke Test 또는 명시적 매핑을 포함하도록 표시를 맞췄습니다. 세 시트에서 기본 Smoke Test·명시적 매핑은 ‘예’, 일반 미연결은 ‘아니요’로 확인했습니다. 실제 SUT 준비는 실행 시 별도 판단합니다. [실행 가능 판정](../src/QAStudio.Application/ResultPackageExporter.cs#L23) · [Excel 내보내기](../src/QAStudio.Adapters/WorkbookAdapter.ResultPackage.cs) · [Sprint 4.1 확인](EXECUTION_RESULTS.md) · [패키지 회귀](../tests/QAStudio.Tests/ResultPackageChecks.cs)

## 실제 운영 ERROR — 실행 전 대상 조건 확인

| 사례 | 재현 조건과 기대 | 기록된 관찰과 원인 범위 | 수정 여부와 재검증 |
| --- | --- | --- | --- |
| 기본 실행 Smoke Test 5건 | 지정 대상이 이미 떠 있는 상태에서 새 시작 실행을 요청합니다. 기존 창을 새 시작의 성공 근거로 쓰지 않아야 합니다. | 모든 Run에 이미 실행 중인 대상이 확인돼 ERROR가 기록됐습니다. 보호 동작을 확인한 환경·사전조건 오류입니다. | 이 오류를 제품 결함 수정으로 집계하지 않습니다. 별도 실행의 PASS가 있으며, 실제 프로세스와 창 불일치 및 타깃 식별 경계는 공개 하니스로 검증합니다. [프로세스 검증](../tests/QAStudio.Tests/Program.cs#L361) · [PID 식별 회귀](../tests/QAStudio.Tests/Program.cs#L402) |
| MAC-QA-002 2건 | 자유 시퀀스가 F 두 회인지 실제 표를 읽은 뒤 F3를 보내고, 메모장에 정확히 ff가 안정적으로 남는지 판정합니다. | 시퀀스 표를 읽지 못해 두 Run 모두 ERROR였습니다. 두 기록에 입력 호출이 없습니다. 당시 표 읽기에 실패한 근본 원인은 확정하지 않았습니다. | 제품 수정 여부는 해당 Run 기록에서 확인할 수 없습니다. 별도 재검증 Run의 실제 ff 관찰과 PASS를 보존했습니다. [사전조건 코드](../src/QA.AutomationStudio.Desktop/NotepadTextProbe.cs#L385) · [실제 재검증](../reports/submission-evidence/MAC-QA-002.redacted.json) · [실패 격리 회귀](../tests/QAStudio.Tests/SingleTextChecks.cs) |

## 기대대로 차단된 부정 테스트와 예방적 회귀

| 조건 | 기대와 실제 검증 | 수정 여부와 재검증 |
| --- | --- | --- |
| 같은 프로젝트에 중복 TC가 있는 XLSX | 기본 차단. 격리 GUI에서 중복 1건이 표시되고 가져오기 버튼이 비활성화됐습니다. 명시적 건너뛰기에선 기존 값이 보존돼야 합니다. | 의도된 정책. 게시 EXE 회귀가 파일 내부·DB 중복과 저장 직전 재충돌을 각각 검사합니다. [구현](../src/QA.AutomationStudio.Desktop/ImportVerification.cs) |
| 실행 이력 없는 TC에 Excel 판정 텍스트가 있음 | 가져오기가 Run을 만들지 않으며 NOT RUN을 유지합니다. | 예방 검증. 하니스가 원문 PASS와 최신 판정을 구분하고, 격리 수동 QAT-003도 별도로 검증했습니다. [테스트](../tests/QAStudio.Tests/Program.cs#L211) · [수동 기록](MANUAL_READINESS.md) |
| 매핑 저장, 재시작, 변경, 해제 | 프로젝트·TC별 유일 매핑, 기존 TC와 Run 보존 | 회귀에서 재시작·변경·해제 후 결과를 확인했습니다. 장애 보고로 집계하지 않습니다. [테스트](../tests/QAStudio.Tests/MappingChecks.cs#L66) |
| 합성 증적 누락 또는 기록 해시 불일치 | 패키지 인덱스에 상태를 표기하고 원래 판정과 원본 파일을 유지합니다. | 예방 검증. 인덱스의 Missing·RecordedHashMismatch와 원본 해시를 확인합니다. [테스트](../tests/QAStudio.Tests/ResultPackageChecks.cs#L91) |
| 결과 Excel 쓰기 오류를 주입 | 불완전 패키지를 게시하지 않으며 저장 DB를 유지합니다. | 예방 검증. 예외 후 출력 폴더와 기존 DB 해시를 검사합니다. [테스트](../tests/QAStudio.Tests/ResultPackageChecks.cs#L102) |

[실행 결과](EXECUTION_RESULTS.md) · [QA 테스트 전략](QA_TEST_STRATEGY.md)
