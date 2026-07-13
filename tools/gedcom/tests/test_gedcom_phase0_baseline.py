"""Phase 0 regression fixture tests for gedcom_tool.py.

See docs/POSTGRESQL_GENEALOGY_WORKSPACE_PLAN.md, Phase 0 and section 12
("Minimum fixtures"). This fixture (phase0_baseline.ged) is a synthetic,
4-generation GEDCOM covering: Cyrillic Ukrainian patronymic names, Polish
(Latin) names, a bilingual slash name, exact/ABT/BET-AND/BEF/AFT dates, one
invalid date string, one disconnected person, one remarriage (person as
spouse in two FAM records), event citations (SOUR structures), several MARR
events with dates and places, and a family with children but no marriage
record.

These tests pin down the exact counts gedcom_tool.py currently produces for
this fixture, so that any future change to the parser (including the planned
PostgreSQL/Phase 3 port) is caught if it silently changes behavior.
"""

import csv
import json
import sys
import tempfile
import unittest
from pathlib import Path


# gedcom_tool.py lives one directory up (Genealogy.Workspace/tools/gedcom).
TOOL_DIR = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(TOOL_DIR))

import gedcom_tool  # noqa: E402


FIXTURE = Path(__file__).resolve().parent / "fixtures" / "phase0_baseline.ged"

# Fixed identifiers so repeated runs are directly comparable.
BATCH_ID = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"
TREE_ID = "cccccccc-cccc-cccc-cccc-cccccccccccc"


class GedcomPhase0BaselineTests(unittest.TestCase):
    def test_inspect_summary_counts(self):
        with tempfile.TemporaryDirectory() as tmp:
            out_dir = Path(tmp)
            exit_code = gedcom_tool.main(["inspect", str(FIXTURE), str(out_dir)])

            self.assertEqual(exit_code, 0)
            summary = json.loads((out_dir / "summary.json").read_text(encoding="utf-8"))

            self.assertEqual(summary["personCount"], 28)
            self.assertEqual(summary["familyCount"], 8)
            self.assertEqual(summary["distinctPlaceCount"], 6)
            self.assertEqual(summary["unresolvedReferenceCount"], 0)
            self.assertEqual(summary["nameCollisionCount"], 0)
            self.assertEqual(summary["eventCountIncludingDerived"], 83)
            self.assertEqual(summary["placeLinkedEventCount"], 72)
            self.assertEqual(
                summary["eventTypeCounts"],
                {"CHILD_BIRTH": 36, "BIRT": 28, "MARR": 14, "DEAT": 5},
            )

    def test_export_staging_tsv_is_deterministic_across_runs(self):
        person_uuid_sets = []
        manifest_counts = []

        for _ in range(2):
            with tempfile.TemporaryDirectory() as tmp:
                out_dir = Path(tmp)
                exit_code = gedcom_tool.main([
                    "export-staging-tsv",
                    str(FIXTURE),
                    str(out_dir),
                    "--batch-id",
                    BATCH_ID,
                    "--tree-id",
                    TREE_ID,
                ])
                self.assertEqual(exit_code, 0)

                manifest = json.loads((out_dir / "staging_manifest.json").read_text(encoding="utf-8"))
                manifest_counts.append(manifest["counts"])

                with (out_dir / "gedcom_import_person.tsv").open(
                    encoding="utf-16",
                    newline="",
                ) as handle:
                    rows = list(csv.DictReader(handle, delimiter="\t"))

                person_uuid_sets.append([row["TreePersonId"] for row in rows])

        # Same counts every run.
        self.assertEqual(manifest_counts[0], manifest_counts[1])
        self.assertEqual(manifest_counts[0]["gedcom_import_person.tsv"], 28)
        self.assertEqual(manifest_counts[0]["gedcom_import_family.tsv"], 8)
        self.assertEqual(manifest_counts[0]["gedcom_import_place.tsv"], 6)
        self.assertEqual(manifest_counts[0]["gedcom_import_person_name_parsed.tsv"], 29)
        self.assertEqual(manifest_counts[0]["gedcom_import_event_citation.tsv"], 2)
        self.assertEqual(manifest_counts[0]["date_parse_warnings.tsv"], 22)

        # Deterministic UUIDv5 person identifiers: same xref + same batch/tree
        # inputs must yield byte-identical TreePersonId values run over run.
        self.assertEqual(len(person_uuid_sets[0]), 28)
        self.assertEqual(person_uuid_sets[0], person_uuid_sets[1])
        # And they must actually be distinct per person, not all collapsed
        # to one value.
        self.assertEqual(len(set(person_uuid_sets[0])), 28)

    def test_date_warnings_include_invalid_date_row(self):
        with tempfile.TemporaryDirectory() as tmp:
            out_dir = Path(tmp)
            exit_code = gedcom_tool.main([
                "export-staging-tsv",
                str(FIXTURE),
                str(out_dir),
                "--batch-id",
                BATCH_ID,
                "--tree-id",
                TREE_ID,
            ])
            self.assertEqual(exit_code, 0)

            with (out_dir / "date_parse_warnings.tsv").open(
                encoding="utf-16",
                newline="",
            ) as handle:
                warnings = list(csv.DictReader(handle, delimiter="\t"))

            invalid_rows = [row for row in warnings if row["PersonXref"] == "@I5@"]
            self.assertEqual(len(invalid_rows), 1)
            invalid = invalid_rows[0]
            self.assertEqual(invalid["EventType"], "DEAT")
            self.assertEqual(invalid["DateRaw"], "NEVER")
            self.assertEqual(invalid["DateStatus"], "UNPARSED")
            self.assertEqual(invalid["WarningKind"], "UNPARSED")
            self.assertEqual(invalid["DateFrom"], "")
            self.assertEqual(invalid["DateTo"], "")

            # Spot-check the other date-warning categories the fixture is
            # meant to exercise are all present at least once.
            kinds = {row["WarningKind"] for row in warnings}
            self.assertEqual(kinds, {"APPROXIMATE", "RANGE", "OPEN_BOUND", "UNPARSED"})


if __name__ == "__main__":
    unittest.main()
