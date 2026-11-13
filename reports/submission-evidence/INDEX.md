# 공개 자동화 실행 증적

Sprint 4.1 · 2026-11-13 KST

아래 5건은 기존 MacroStudio v6.3 QA와 Windows 메모장 F3 E2E의 PASS 기록입니다. Sprint 4.1에서는 운영 DB의 Run ID·매핑·등록 해시를 원본 JSON과 대조했습니다.

| Project / TC ID | Automation Scenario ID | PASS Run ID | 판정 | 실제 관찰 | GitHub 기준 상대 경로 | 파일·무결성 |
| --- | --- | --- | --- | --- | --- | --- |
| `macro-studio` / `MAC-QA-001` | `MAC-NP-001` | `TEXT-20261105T005548006-f4b371a5ee6c4f51afc004f04eddc85a` | PASS | F3 1회 후 정확히 `f`, 2초 이상 유지 | [`reports/submission-evidence/MAC-QA-001.redacted.json`](MAC-QA-001.redacted.json) | 공개 사본 확인. 원본은 SQLite 등록 해시와 일치 |
| `macro-studio` / `MAC-QA-002` | `MAC-NP-002` | `TEXT-20261109T014455758-553d458912cc45239845ab02e9f505af` | PASS | F3 1회 후 정확히 `ff`, 2초 이상 유지 | [`reports/submission-evidence/MAC-QA-002.redacted.json`](MAC-QA-002.redacted.json) | 공개 사본 확인. 원본은 SQLite 등록 해시와 일치 |
| `macro-studio` / `MAC-QA-003` | `MAC-NP-003` | `TEXT-20261109T020318417-c5dbcd7f19cd4cc0aa5a2b1b0f5d7163` | PASS | 매크로 OFF에서 F3 1회 후 빈 문서 2,603 ms 유지 | [`reports/submission-evidence/MAC-QA-003.redacted.json`](MAC-QA-003.redacted.json) | 공개 사본 확인. 원본은 SQLite 등록 해시와 일치 |
| `macro-studio` / `MAC-QA-004` | `MAC-NP-004` | `TEXT-20261109T023059649-e31fdd3c69af48c48f51f5f3614f7378` | PASS | 매크로 ON·허용 대상 불일치에서 F3 1회 후 빈 문서 3,472 ms 유지 | [`reports/submission-evidence/MAC-QA-004.redacted.json`](MAC-QA-004.redacted.json) | 공개 사본 확인. 원본은 SQLite 등록 해시와 일치 |
| `macro-studio` / `MAC-QA-005` | `MAC-NP-005` | `TEXT-20261110T015547431-8237dbcebb52432584bead182d384396` | PASS | 첫 F3 후 `f`, 두 번째 F3 후 `ff`, 최종 관찰 2,091 ms 유지 | [`reports/submission-evidence/MAC-QA-005.redacted.json`](MAC-QA-005.redacted.json) | 공개 사본 확인. 원본은 SQLite 등록 해시와 일치 |

| TC ID | 원본 JSON SHA-256 | 제출용 비식별 JSON SHA-256 |
| --- | --- | --- |
| `MAC-QA-001` | `82eb4cb5c56c83bad64cbba4615c20e2208cd88312519630104dd8519cf404d7` | `f4f257e93aa82e7a05ae514fcc827b10f470a71e8dd2b69468e466bff8caa8ca` |
| `MAC-QA-002` | `d1c499711180454348b42315182d1cc05cc0d0769994d553d530442cd10cd182` | `89f94a0919a906bee5cd2572b88f725c90eec3b2d42e9937969ac8576138b936` |
| `MAC-QA-003` | `3c365c4bf3da93667f56ba0b8e9446cecd79a6bf3d9324c6f7f9761d43291f13` | `c48f44cb12da1c4b8a3f12804b519adb6b5002674b16fe5f030502e6fc952307` |
| `MAC-QA-004` | `710ce59fe50f145ac101d12843216b8e3e313f8948cd36d9362a902a54343c53` | `e6d3823d69ffe64b1ebed8cf8a05715c882ab48178fbadb56139c7c45c8a74bc` |
| `MAC-QA-005` | `86bcd0b05428841510a6bec3c9ad98692402192048b2c7e94ccb52ae8e9c60d6` | `d80b235b623a2ea2d6a33ed40b0301967435d387a15e40593460a9411e4dbd05` |

제출용 JSON은 원본에서 식별 정보가 없는 판정·트리거·텍스트 관찰 필드를 발췌했습니다. 원본의 절대 경로, 명령줄, 실행 환경·프로필 세부 정보, PID·창 핸들·문서 식별자, 자유 형식 로그와 TC 스냅샷은 제외했습니다. 문서·읽기 방식의 일치 여부는 원본 표본을 대조한 값입니다. 사본의 SHA-256은 위 표에서 별도로 확인할 수 있습니다.

공개 파일 경로는 이 저장소 루트 기준입니다.
