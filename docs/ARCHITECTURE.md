# 시스템 아키텍처

| 계층 | 책임 |
| --- | --- |
| [WinForms](../src/QA.AutomationStudio.Desktop/) | TC 검색, 가져오기 미리보기, 매핑, 실행 및 수동 결과 등록 |
| [Application](../src/QAStudio.Application/) | 실행 오케스트레이션, 수동 결과 기록, 결과 패키지 생성 |
| [Domain](../src/QAStudio.Domain/) | TC, Run, 판정, 증적과 수동 준비도 모델 |
| [Infrastructure](../src/QAStudio.Infrastructure/) | SQLite schema v6, 프로젝트 범위 저장, 자동 Run·증적 트랜잭션 |
| [Adapters](../src/QAStudio.Adapters/) | Excel OOXML, Windows 대상 프로세스와 창 관찰 |

## 실행과 내보내기

프로젝트·TC ID를 선택하고 명시적 매핑 또는 기본 Smoke Test를 실행합니다. F3 시나리오는 대상 프로세스, 빈 메모장 문서와 포커스를 확인하고, SendInput으로 F3를 전송한 뒤 UI Automation으로 같은 문서를 읽습니다. 실행기는 실제 관찰을 판정하고 JSON을 기록한 다음 SQLite에 Run과 증적을 연결합니다.

결과 패키지는 SQLite 스냅샷을 읽어 마스터 Excel, 증적 인덱스, 결과 Excel을 생성합니다. 결과 Excel에는 TC별 최신 상태와 자동·수동 이력이 포함됩니다. [내보내기 구현](../src/QAStudio.Application/ResultPackageExporter.cs) · [증적 추적](EVIDENCE_INTEGRITY.md)

## 데이터·실행 경계

- **설계에서 등록까지** — Excel 열을 표준 필드에 연결하고 미리보기에서 필수값·중복을 검사합니다. 검증된 행의 신규 등록과 원본 워크북 출처는 SQLite 트랜잭션으로 보존합니다. [가져오기 흐름](../src/QA.AutomationStudio.Desktop/DesktopImportSession.cs)
- **등록에서 실행까지** — 설계 행과 명시적 매핑은 별도입니다. 실행 시 대상 프로세스, 창, 시나리오 사전조건을 확인합니다. 수동 결과는 실행자 관찰 및 첨부와 함께 별도 이력에 저장됩니다. [자동 실행](../src/QAStudio.Application/) · [결과 모델](../src/QAStudio.Domain/Entities.cs)
- **저장에서 보고까지** — Run과 증적을 연결하고 결과 집계 및 패키지를 분리합니다. 내보내기 중 불완전 결과가 남지 않는지와 SQLite 무결성은 공개용 격리 검증으로 확인합니다. [패키지 회귀](../tests/QAStudio.Tests/ResultPackageChecks.cs) · [공개 Windows CI](VERIFICATION.md)
