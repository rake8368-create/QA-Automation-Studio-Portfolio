# Sprint 4.1까지의 검증 결과

| 범위 | 확인 결과 |
| --- | --- |
| 운영 SQLite | schema v6, TC 76건, 명시적 매핑 5건, 자동 Run·등록 증적 각 17건, 수동 Run 0건 |
| 운영 자동 Run 전체 | PASS 10건, ERROR 7건 |
| 운영 TC별 최신 판정 | PASS 6건, NOT RUN 70건 |
| 격리 수동 GUI | `MAC-016`, `QAT-001`, `QAT-003` 각각 PASS |
| Sprint 3.2 UI | 연결 TC 실행 활성, 일반 미연결 TC 차단, 격리 매핑 저장·해제 후 재시작 확인 |
| Sprint 4.1 Excel | `TC_Master`, `TC_Results`, `Automation_Review`에서 기본 Smoke Test와 명시적 매핑은 예, 미연결 TC는 아니요 |
| Sprint 4.1 Release | Windows 11 x64에서 Release 빌드 경고·오류 0건. 로컬 자체 포함 EXE 게시 후 `start.cmd` 시작 및 운영 프로젝트 2개 읽기 확인 |

최신 PASS 6건은 매핑 TC 5건과 기본 Smoke Test 1건입니다. 누적 ERROR 7건에는 Smoke Test 5건과 `MAC-QA-002` 사전조건 확인 오류 2건이 포함됩니다. 격리 수동 판정은 운영 집계에 포함되지 않습니다. Sprint 4.1은 기존 F3 결과를 보존했으며, 이 표의 결과는 해당 로컬 검증 환경에서 관찰했습니다. [F3 증적](../reports/submission-evidence/INDEX.md) · [수동 준비도](MANUAL_READINESS.md)

## 집계 기준

| 집계 | 의미 |
| --- | --- |
| 설계 워크북 70건 | 공개 입력 샘플. 공개 기본 하니스의 첫 등록에서는 전부 NOT RUN |
| 운영 등록 76건 | Sprint 4.1 운영 DB의 등록 TC. 누적 자동 Run 17건은 재시도 포함 |
| 수동 준비도 71건 | 명시적 매핑 5건을 제외한 실행 준비 조건 분류 |
| 기존 실제 F3 증적 5건 | 운영 이력에서 공개가 가능한 PASS 사본. 기존 ERROR 기록은 원본에 보존 |
| 공개 CI | 실행형 하니스 74/74, 게시 EXE 가져오기 10/10 및 패키지 회귀 모드. 실제 F3 0건 |

[실제 오류의 원인과 재검증](DEFECT_CASE_STUDIES.md) · [공개 CI 상세 범위](VERIFICATION.md)
