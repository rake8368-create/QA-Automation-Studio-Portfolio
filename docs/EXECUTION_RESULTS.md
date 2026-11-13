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
