# 수동 테스트 준비도

운영 TC 76건에서 명시적 매핑 5건을 제외한 71건을 실행 준비 조건으로 분류했습니다.

| 준비도 | 건수 | 필요한 조건 |
| --- | ---: | --- |
| GUI_MANUAL | 3 | 기존 GUI에서 조작과 관찰 가능 |
| ENVIRONMENT_REQUIRED | 19 | 격리 프로필, 경계값 파일, 관찰 도구 등 |
| HARNESS_REQUIRED | 43 | FakeClock, HttpMock, TestHost, 입력 계측 또는 오류 주입 등 |
| REVIEW_REQUIRED | 6 | 대상 버전의 기능이나 판정 기준 확인 |

격리 GUI 검증에서 `MAC-016`은 중복 실행 경고 및 두 번째 프로세스 종료, `QAT-001`은 XLSX 열 매핑 및 중복 저장 차단, `QAT-003`은 미실행 TC의 NOT RUN 유지를 확인했습니다. 세 건은 각각 PASS로 기록했고 운영 수동 이력은 0건입니다. 나머지 68건의 개별 전체 절차는 미검증입니다. [수동 테스트 설계 워크북](../test-data/QAStudio_Manual_TestCases_71_EN.xlsx)
