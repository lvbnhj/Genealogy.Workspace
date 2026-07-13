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


class GedcomNamePreflightTests(unittest.TestCase):
    def test_parses_gedcom_dates_to_ranges(self):
        exact = gedcom_tool.parse_gedcom_date("12 OCT 1798")
        self.assertEqual(exact.date_from, "1798-10-12")
        self.assertEqual(exact.date_to, "1798-10-12")
        self.assertEqual(exact.year_from, 1798)
        self.assertEqual(exact.year_to, 1798)

        localized = gedcom_tool.Event(tag="MARR")
        gedcom_tool.set_event_date(localized, "9 Січ 1847")
        self.assertEqual(localized.date_from, "1847-01-09")
        self.assertEqual(localized.date_to, "1847-01-09")
        self.assertEqual(gedcom_tool.date_warning_kind(localized), "")

        year = gedcom_tool.parse_gedcom_date("ABT 1850")
        self.assertEqual(year.date_from, "1850-01-01")
        self.assertEqual(year.date_to, "1850-12-31")
        self.assertEqual(year.modifier, "ABT")

        date_range = gedcom_tool.parse_gedcom_date("BET 1801 AND 1805")
        self.assertEqual(date_range.date_from, "1801-01-01")
        self.assertEqual(date_range.date_to, "1805-12-31")

        before = gedcom_tool.parse_gedcom_date("До 1846")
        self.assertIsNone(before.date_from)
        self.assertEqual(before.date_to, "1846-12-31")
        self.assertEqual(before.modifier, "BEF")
        before_event = gedcom_tool.Event(tag="MARR")
        gedcom_tool.set_event_date(before_event, "До 1846")
        self.assertEqual(gedcom_tool.date_warning_kind(before_event), "OPEN_BOUND")

        open_ended = gedcom_tool.parse_gedcom_date("BET 1 MAY 1777 AND AFT 1800")
        self.assertEqual(open_ended.date_from, "1777-05-01")
        self.assertIsNone(open_ended.date_to)
        approximate = gedcom_tool.Event(tag="BIRT")
        gedcom_tool.set_event_date(approximate, "ABT 1850")
        self.assertEqual(gedcom_tool.date_warning_kind(approximate), "APPROXIMATE")

    def test_derived_events_keep_parsed_date_ranges(self):
        birth = gedcom_tool.Event(tag="BIRT")
        gedcom_tool.set_event_date(birth, "1844")
        child = gedcom_tool.Indi(xref="@C@", names=["Child /Person/"], events=[birth])
        father = gedcom_tool.Indi(xref="@F@", names=["Father /Person/"])
        family = gedcom_tool.Fam(xref="@FAM@", husb="@F@", chil=["@C@"])
        ged = gedcom_tool.Gedcom(
            indis={"@C@": child, "@F@": father},
            fams={"@FAM@": family},
            sources={},
            unresolved_refs=[],
        )

        derived = [
            event for xref, event in gedcom_tool.iter_person_events(ged)
            if xref == "@F@" and event.tag == "CHILD_BIRTH"
        ][0]

        self.assertEqual(derived.date_from, "1844-01-01")
        self.assertEqual(derived.date_to, "1844-12-31")

    def test_normalizes_place_keys_without_admin_noise(self):
        self.assertEqual(
            gedcom_tool.normalize_place_key("с. Немиринці, Летичівський пов., Подільська губ."),
            "немиринці, летичівськии, подільська",
        )
        self.assertEqual(
            gedcom_tool.normalize_place_key("Nemyrintsi; Letychiv powiat; Podolia gubernia"),
            "nemyrintsi, letychiv, podolia",
        )

    def test_normalizes_given_surname_and_patronymic(self):
        variant = gedcom_tool.build_name_variants("Іоанн Іванович /Сімашко/")[0]

        normalized = gedcom_tool.normalize_name_for_preflight(
            "Іоанн Іванович /Сімашко/",
            variant,
        )

        self.assertEqual(normalized.given_name, "Іоанн")
        self.assertEqual(normalized.patronymic, "Іванович")
        self.assertEqual(normalized.surname, "Сімашко")
        self.assertEqual(normalized.given_name_normalized, "іван")
        self.assertEqual(normalized.surname_normalized, "семашко")
        self.assertEqual(normalized.full_name_normalized, "іван іванович семашко")
        self.assertEqual(normalized.parser_status, "OK")
        self.assertIn("given_name:Іоанн->Іван", normalized.variant_explanation)
        self.assertIn("surname:Сімашко->Семашко", normalized.variant_explanation)

    def test_normalizes_latin_variants_to_canonical_search_keys(self):
        variant = gedcom_tool.build_name_variants("Jan /Siemaszko/")[0]

        normalized = gedcom_tool.normalize_name_for_preflight("Jan /Siemaszko/", variant)

        self.assertEqual(normalized.script_code, "LAT")
        self.assertEqual(normalized.language_hint, "pl-la")
        self.assertEqual(normalized.given_name_normalized, "іван")
        self.assertEqual(normalized.surname_normalized, "семашко")
        self.assertEqual(normalized.full_name_normalized, "іван семашко")

    def test_normalizes_latin_church_given_name_variants(self):
        variant = gedcom_tool.build_name_variants("Timofeus /Siemaszko/")[0]

        normalized = gedcom_tool.normalize_name_for_preflight("Timofeus /Siemaszko/", variant)

        self.assertEqual(normalized.script_code, "LAT")
        self.assertEqual(normalized.given_name_normalized, "тимофеи")
        self.assertEqual(normalized.surname_normalized, "семашко")
        self.assertEqual(normalized.full_name_normalized, "тимофеи семашко")
        self.assertIn("given_name:Timofeus->Тимофей", normalized.variant_explanation)

    def test_builds_script_matched_variants_for_bilingual_slash_names(self):
        variants = gedcom_tool.build_name_variants("Іван/Jan /Семашко/Siemaszko/")

        self.assertEqual(
            [(v["ScriptCode"], v["Given"], v["Surname"], v["FullNameNormalized"]) for v in variants],
            [
                ("CYR", "Іван", "Семашко", "іван семашко"),
                ("LAT", "Jan", "Siemaszko", "jan siemaszko"),
            ],
        )

        normalized = [
            gedcom_tool.normalize_name_for_preflight("Іван/Jan /Семашко/Siemaszko/", v)
            for v in variants
        ]

        self.assertEqual(normalized[0].full_name_normalized, "іван семашко")
        self.assertEqual(normalized[0].surname_normalized, "семашко")
        self.assertEqual(normalized[1].full_name_normalized, "іван семашко")
        self.assertEqual(normalized[1].surname_normalized, "семашко")

    def test_placeholder_slash_surname_does_not_become_surname_key(self):
        variants = gedcom_tool.build_name_variants("Людмила //")

        self.assertEqual(len(variants), 1)
        self.assertEqual(variants[0]["Given"], "Людмила")
        self.assertEqual(variants[0]["Surname"], "")

        normalized = gedcom_tool.normalize_name_for_preflight("Людмила //", variants[0])

        self.assertEqual(normalized.given_name_normalized, "людмила")
        self.assertEqual(normalized.surname_normalized, "")
        self.assertEqual(normalized.full_name_normalized, "людмила")

    def test_marks_uncertain_names_as_ambiguous(self):
        variant = gedcom_tool.build_name_variants("І[пишов?] /Семашко/")[0]

        normalized = gedcom_tool.normalize_name_for_preflight(
            "І[пишов?] /Семашко/",
            variant,
        )

        self.assertEqual(normalized.parser_status, "AMBIGUOUS")
        self.assertLess(normalized.normalization_confidence, 0.75)
        self.assertIn("uncertainty-marker", normalized.variant_explanation)

    def test_export_staging_writes_parsed_names(self):
        fixture = Path(__file__).resolve().parent / "fixtures" / "preflight_names.ged"
        batch_id = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
        tree_id = "dddddddd-dddd-dddd-dddd-dddddddddddd"

        with tempfile.TemporaryDirectory() as tmp:
            out_dir = Path(tmp)
            # --tree-id is now required; --legacy-ids preserves the original
            # bare-xref UUID form this test was written against.
            exit_code = gedcom_tool.main([
                "export-staging-tsv",
                str(fixture),
                str(out_dir),
                "--batch-id",
                batch_id,
                "--tree-id",
                tree_id,
                "--legacy-ids",
            ])

            self.assertEqual(exit_code, 0)
            manifest = json.loads((out_dir / "staging_manifest.json").read_text(encoding="utf-8"))
            self.assertIn("gedcom_import_person_name_parsed.tsv", manifest["loadOrder"])
            self.assertIn("gedcom_import_event_citation.tsv", manifest["loadOrder"])
            self.assertIn("date_parse_warnings.tsv", manifest["loadOrder"])
            self.assertEqual(manifest["artifacts"]["dateParseWarnings"], "date_parse_warnings.tsv")
            self.assertEqual(manifest["counts"]["gedcom_import_person_name_parsed.tsv"], 8)
            self.assertEqual(manifest["counts"]["gedcom_import_event.tsv"], 3)
            self.assertEqual(manifest["counts"]["gedcom_import_event_citation.tsv"], 2)

            with (out_dir / "gedcom_import_person_name_parsed.tsv").open(
                encoding="utf-16",
                newline="",
            ) as handle:
                rows = list(csv.DictReader(handle, delimiter="\t"))

            jan = next(row for row in rows if row["RawName"] == "Jan /Siemaszko/")
            self.assertEqual(jan["GivenNameNormalized"], "іван")
            self.assertEqual(jan["SurnameNormalized"], "семашко")
            self.assertEqual(jan["ParserStatus"], "OK")

            ambiguous = next(row for row in rows if row["RawName"] == "І[пишов?] /Семашко/")
            self.assertEqual(ambiguous["ParserStatus"], "AMBIGUOUS")

            bilingual = [
                row for row in rows
                if row["RawName"] == "Іван/Jan /Семашко/Siemaszko/"
            ]
            self.assertEqual(len(bilingual), 2)
            self.assertEqual({row["FullNameNormalized"] for row in bilingual}, {"іван семашко"})

            slash_placeholder = next(row for row in rows if row["RawName"] == "Людмила //")
            self.assertEqual(slash_placeholder["SurnameNormalized"], "")

            with (out_dir / "gedcom_import_event.tsv").open(
                encoding="utf-16",
                newline="",
            ) as handle:
                events = list(csv.DictReader(handle, delimiter="\t"))

            birth = next(row for row in events if row["EventType"] == "BIRT" and row["DateRaw"] == "1801")
            self.assertEqual(birth["DateRaw"], "1801")
            self.assertEqual(birth["DateFrom"], "1801-01-01")
            self.assertEqual(birth["DateTo"], "1801-12-31")

            with (out_dir / "gedcom_import_event_citation.tsv").open(
                encoding="utf-16",
                newline="",
            ) as handle:
                citations = list(csv.DictReader(handle, delimiter="\t"))

            birth_citation = next(row for row in citations if row["EventRowNumber"] == birth["RowNumber"])
            self.assertEqual(birth_citation["SourceRef"], "@S1@")
            self.assertEqual(birth_citation["SourceTitle"], "Немиринецька метрична книга")
            self.assertEqual(birth_citation["Page"], "ф.1, оп.2, спр.3, арк.4")
            self.assertEqual(birth_citation["Quality"], "3")
            self.assertEqual(birth_citation["CitationDateRaw"], "1900")
            self.assertEqual(
                birth_citation["CitationText"],
                "Метричний запис про народження Іоанна Хрещення в Немиринцях",
            )


if __name__ == "__main__":
    unittest.main()
