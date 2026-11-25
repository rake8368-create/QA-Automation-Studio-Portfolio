"""Public-only validation and fixtures for the existing Windows QA harness."""
import argparse
from contextlib import closing
import hashlib
import json
from pathlib import Path
import re
import shutil
import sqlite3
import subprocess
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]
MANIFEST = "PUBLIC_MANIFEST.json"
WORKBOOK = ROOT / "test-data/QA_Automation_Portfolio_TC_70.xlsx"
NS = "{http://schemas.openxmlformats.org/spreadsheetml/2006/main}"
SHEET = "xl/worksheets/sheet2.xml"


def require(ok, message):
    if not ok:
        raise ValueError(message)


def load(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def sha(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def manifest(update=False):
    file = ROOT / MANIFEST
    current = load(file)
    require(current["algorithm"] == "SHA-256" and current["self_excluded"] == MANIFEST, "Unexpected manifest format")
    paths = subprocess.check_output(["git", "ls-files", "-z"], cwd=ROOT).decode().split("\0")
    previous = {item["path"]: item["classification"] for item in current["files"]}
    files = []
    for name in sorted(set(paths) - {"", MANIFEST}):
        category = "build-and-config"
        if name.startswith("docs/"):
            category = "public-documentation"
        elif name.startswith("reports/screenshots/"):
            category = "screen-capture-redacted"
        elif name.startswith("portfolio/"):
            category = "validated-portfolio-pdf"
        target = ROOT / name
        require(target.is_file() and not target.is_symlink(), f"Invalid public file: {name}")
        files.append({"path": name, "bytes": target.stat().st_size, "sha256": sha(target),
                      "classification": previous.get(name, category)})
    if update:
        current["files"] = files
        file.write_text(json.dumps(current, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
    else:
        require(len(current["files"]) == len(files) and {i["path"]: i for i in files} ==
                {i["path"]: i for i in current["files"]}, "Public file set, size or SHA-256 differs")
    return len(files)


def historical_evidence():
    directory = ROOT / "reports/submission-evidence"
    index = (directory / "INDEX.md").read_text(encoding="utf-8-sig")
    tick = chr(96)
    pattern = rf"\| {tick}(MAC-QA-\d+){tick} \| {tick}([0-9a-f]{{64}}){tick} \| {tick}([0-9a-f]{{64}}){tick} \|"
    entries = re.findall(pattern, index)
    require(len(entries) == 5 and len({key for key, _, _ in entries}) == 5, "Expected five indexed hashes")
    run_ids = set()
    for case, source_hash, public_hash in entries:
        path = directory / f"{case}.redacted.json"
        doc = load(path)
        require(sha(path) == public_hash and doc["sourceSha256"].lower() == source_hash
                and doc["tcId"] == case and doc["result"] == "PASS", f"Public evidence mismatch: {case}")
        run_ids.add(doc["runId"])
    require(len(run_ids) == 5 and len(list(directory.glob("*.redacted.json"))) == 5, "Unexpected F3 evidence set")
    return len(entries)


def fixtures(directory):
    fixture_dir = directory / "fixtures"
    fixture_dir.mkdir()
    with zipfile.ZipFile(WORKBOOK) as original:
        sheet_bytes = original.read(SHEET)
        workbook_path = "xl/workbook.xml"
        metadata = ET.fromstring(original.read(workbook_path))
        sheets = metadata.find(NS + "sheets")
        sheets.remove(next(sheet for sheet in sheets if sheet.get("name") == "Auto_Design"))
        workbook_bytes = ET.tostring(metadata, encoding="utf-8")

        def workbook(name, ids, overrides=None):
            sheet = ET.fromstring(sheet_bytes)
            data = sheet.find(NS + "sheetData")
            for row in list(data):
                if int(row.get("r")) > 5 + len(ids):
                    data.remove(row)

            def set_cell(reference, value):
                cell = sheet.find(f".//{NS}c[@r='{reference}']")
                require(cell is not None, f"Missing template cell: {reference}")
                cell.set("t", "inlineStr")
                for child in list(cell):
                    cell.remove(child)
                ET.SubElement(ET.SubElement(cell, NS + "is"), NS + "t").text = value

            for number, case in enumerate(ids, 6):
                set_cell(f"A{number}", case)
                set_cell(f"C{number}", "Synthetic fixture purpose")
                set_cell(f"I{number}", "Synthetic fixture expected result")
            for reference, value in (overrides or {}).items():
                set_cell(reference, value)
            with zipfile.ZipFile(fixture_dir / f"{name}.xlsx", "w", zipfile.ZIP_DEFLATED) as result:
                changed = {SHEET: ET.tostring(sheet, encoding="utf-8"), workbook_path: workbook_bytes}
                for member in original.namelist():
                    result.writestr(member, changed[member] if member in changed else original.read(member))

        workbook("valid", ["IMP-GUI-20261028-001"])
        workbook("missing-column", ["CI-MISSING"], {"I5": "Unknown required heading"})
        workbook("duplicate-file", ["CI-DUPLICATE", "CI-DUPLICATE"])
        workbook("duplicate-db", ["MAC-001", "CI-NEW"])
        workbook("rollback", ["CI-ROLLBACK-FIRST", "CI-ROLLBACK-SECOND"])
        workbook("alias", ["CI-ALIAS"], {"A5": "Identifier", "C5": "Purpose"})
        workbook("blank-required", ["CI-BLANK"], {"I6": ""})
        workbook("bad-priority", ["CI-PRIORITY"], {"E6": "HIGH"})
    (fixture_dir / "invalid.xlsx").write_bytes(b"Not an OOXML workbook")
    rollback = directory / "rollback.sqlite"
    shutil.copyfile(directory / "before.sqlite", rollback)
    with closing(sqlite3.connect(rollback)) as connection:
        connection.execute("""CREATE TRIGGER ci_abort_second BEFORE INSERT ON cases
                              WHEN NEW.id = 'CI-ROLLBACK-SECOND'
                              BEGIN SELECT RAISE(ABORT, 'synthetic rollback fixture'); END""")
        connection.commit()


def result_count(data, key, minimum):
    items = data["tests"]
    require(len(items) >= minimum and len({item[key] for item in items}) == len(items)
            and all(item["status"] == "PASS" for item in items)
            and data["failed"] == 0 and data["passed"] == len(items)
            and data.get("total", len(items)) == len(items), "Harness result JSON does not pass")
    return len(items)


def database(path, expected):
    require(path.is_file(), f"Missing fixture database: {path.name}")
    with closing(sqlite3.connect(path.resolve().as_uri() + "?mode=ro", uri=True)) as conn:
        require(conn.execute("PRAGMA user_version").fetchone() == (6,), "SQLite schema is not v6")
        require(conn.execute("PRAGMA integrity_check").fetchall() == [("ok",)], "SQLite integrity check failed")
        require(not conn.execute("PRAGMA foreign_key_check").fetchall(), "SQLite foreign key check failed")
        require(tuple(conn.execute(f"SELECT count(*) FROM {table}").fetchone()[0]
                      for table in ("cases", "runs", "manual_runs", "imports")) == expected, "Unexpected fixture counts")


def results(root):
    core = load(root / "core/tests.json")
    base_count = result_count(core, "name", 74)
    require(core["sourceSha256"].lower() == sha(WORKBOOK)
            and core["productTestCasesExecuted"] == 0 and core["productTestCasesNotRun"] == 70, "Workbook or execution scope changed")
    import_count = result_count(load(root / "desktop-results/acceptance.json"), "id", 10)
    packages = list((root / "package").glob("*/output/QA-*"))
    require(len(packages) == 2, "Result package mode did not produce two packages")
    for package in packages:
        require((package / "01_TC_Master/QAStudio_TC_Master.xlsx").is_file()
                and (package / "03_Test_Results/QAStudio_Test_Results.xlsx").is_file()
                and len(list(package.glob("02_Execution_Evidence/**/evidence-index.json"))) == 6, "Package outputs missing")
    database(root / "core/portfolio.sqlite", (70, 0, 0, 1))
    database(root / "desktop-fixture/before.sqlite", (70, 0, 0, 1))
    database(root / "desktop-fixture/rollback.sqlite", (70, 0, 0, 1))
    database(root / "desktop-results/valid.sqlite", (71, 0, 0, 2))
    synthetic_db, = (root / "package").glob("*/fixture.sqlite")
    database(synthetic_db, (6, 6, 0, 1))
    summary = {"executableHarness": {"passed": base_count, "failed": 0},
               "publishedExeImport": {"passed": import_count, "failed": 0},
               "resultPackageFixture": "PASS", "sqliteSchema": 6, "sqliteIntegrity": "PASS",
               "publicManifestFiles": manifest(), "publicHistoricalF3CopiesVerified": historical_evidence(),
               "realF3RunsExecutedInCi": 0, "productDesignCasesExecutedInCi": 0}
    (root / "qa-summary.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(summary, indent=2))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    manifest_parser = commands.add_parser("manifest")
    manifest_parser.add_argument("--update", action="store_true")
    for command in ("fixtures", "results"):
        commands.add_parser(command).add_argument("path", type=Path)
    args = parser.parse_args()
    if args.command == "manifest":
        print(f"Public manifest: {manifest(args.update)}; historical F3 hashes: {historical_evidence()}")
    elif args.command == "fixtures":
        fixtures(args.path)
    else:
        results(args.path)


if __name__ == "__main__":
    main()
