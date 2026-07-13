#!/usr/bin/env python3
# -*- coding: utf-8 -*-

"""
Deterministic, database-neutral GEDCOM preflight tool.

gedcom_tool.py creates compact analysis artifacts (TSV/JSON) for agents and
humans before import/update work starts. It is intentionally database-agnostic:
it produces deterministic staging artifacts only. Loading those artifacts into a
database belongs to the data layer, not to this Python tool.
"""

from __future__ import annotations

import argparse
import calendar
import csv
import hashlib
import json
import os
import re
import unicodedata
import uuid
from collections import Counter, defaultdict, deque
from dataclasses import dataclass, field
from datetime import date
from pathlib import Path
from typing import DefaultDict, Dict, Iterable, List, Optional, Set, Tuple


NAMESPACE_UUID = uuid.UUID("6f8a0d4d-9b9f-4d49-8f2d-2a8c9fe2a2f1")
DATE_PAT_YEAR = re.compile(r"\b(\d{3,4})\b")

STRUCTURAL_INDI_TAGS = {
    "NAME",
    "SEX",
    "FAMC",
    "FAMS",
    "CHAN",
    "SUBM",
    "REFN",
    "RIN",
    "RFN",
    "AFN",
    "ALIA",
    "ANCI",
    "DESI",
    "OBJE",
    "SOUR",
    "NOTE",
}

STRUCTURAL_FAM_TAGS = {
    "HUSB",
    "WIFE",
    "CHIL",
    "CHAN",
    "SUBM",
    "REFN",
    "RIN",
    "NCHI",
    "OBJE",
    "SOUR",
    "NOTE",
}

EVENT_LABELS = {
    "BIRT": "Birth",
    "BAPM": "Baptism",
    "CHR": "Christening",
    "DEAT": "Death",
    "BURI": "Burial",
    "CREM": "Cremation",
    "ADOP": "Adoption",
    "BARM": "Bar mitzvah",
    "BASM": "Bat mitzvah",
    "BLES": "Blessing",
    "CHRA": "Adult christening",
    "CONF": "Confirmation",
    "FCOM": "First communion",
    "GRAD": "Graduation",
    "NATU": "Naturalization",
    "ORDN": "Ordination",
    "RETI": "Retirement",
    "PROB": "Probate",
    "WILL": "Will",
    "CENS": "Census",
    "RESI": "Residence",
    "EMIG": "Emigration",
    "IMMI": "Immigration",
    "OCCU": "Occupation",
    "EDUC": "Education",
    "EVEN": "Event",
    "FACT": "Fact",
    "MARR": "Marriage",
    "DIV": "Divorce",
    "ANUL": "Annulment",
    "ENGA": "Engagement",
    "MARB": "Marriage bann",
    "MARC": "Marriage contract",
    "MARL": "Marriage license",
    "MARS": "Marriage settlement",
    "CHILD_BIRTH": "Birth of child",
}

_CYR_RE = re.compile(r"[\u0400-\u04FF\u0500-\u052F]")
_LAT_RE = re.compile(r"[A-Za-z\u00C0-\u024F\u1E00-\u1EFF]")
_UNCERTAINTY_RE = re.compile(r"\[[^\]]+\]")
_PATRONYMIC_RE = re.compile(
    r"(ович|евич|євич|ич|івна|ївна|овна|евна|євна|ична|івич|yovych|evych|ovych|ovna|evna)$",
    re.IGNORECASE,
)

TITLE_PREFIXES = {
    "пан",
    "пани",
    "пані",
    "госпожа",
    "господин",
    "отець",
    "ієрей",
    "иерей",
    "ксендз",
    "ksiadz",
    "priest",
}

GIVEN_NAME_VARIANT_PAIRS = (
    ("Иоанн", "Іван"),
    ("Іоанн", "Іван"),
    ("Jan", "Іван"),
    ("Joannes", "Іван"),
    ("Ivan", "Іван"),
    ("Фома", "Фома"),
    ("Тома", "Фома"),
    ("Tomasz", "Фома"),
    ("Thomas", "Фома"),
    ("Timofeus", "Тимофей"),
    ("Timotheus", "Тимофей"),
    ("Paraskewa", "Параскева"),
    ("Paraskeva", "Параскева"),
)

SURNAME_VARIANT_PAIRS = (
    ("Сімашко", "Семашко"),
    ("Siemaszko", "Семашко"),
    ("Semaszko", "Семашко"),
)

MONTH_ALIASES = {
    "JAN": 1, "JANUARY": 1, "СІЧ": 1, "СІЧЕНЬ": 1, "ЯНВ": 1, "STY": 1, "STYCZEN": 1,
    "FEB": 2, "FEBRUARY": 2, "ЛЮТ": 2, "ЛЮТИЙ": 2, "ФЕВ": 2, "LUT": 2,
    "MAR": 3, "MARCH": 3, "БЕР": 3, "БЕРЕЗЕНЬ": 3, "МАР": 3, "MARZEC": 3,
    "APR": 4, "APRIL": 4, "КВІ": 4, "КВІТЕНЬ": 4, "АПР": 4, "KWI": 4, "KWIECIEN": 4,
    "MAY": 5, "ТРА": 5, "ТРАВЕНЬ": 5, "МАЙ": 5, "MAJ": 5,
    "JUN": 6, "JUNE": 6, "ЧЕР": 6, "ЧЕРВЕНЬ": 6, "ИЮН": 6, "CZE": 6, "CZERWIEC": 6,
    "JUL": 7, "JULY": 7, "ЛИП": 7, "ЛИПЕНЬ": 7, "ИЮЛ": 7, "LIP": 7, "LIPIEC": 7,
    "AUG": 8, "AUGUST": 8, "СЕР": 8, "СЕРПЕНЬ": 8, "АВГ": 8, "SIE": 8, "SIERPIEN": 8,
    "SEP": 9, "SEPT": 9, "SEPTEMBER": 9, "ВЕР": 9, "ВЕРЕСЕНЬ": 9, "СЕН": 9, "WRZ": 9, "WRZESIEN": 9,
    "OCT": 10, "OCTOBER": 10, "ЖОВ": 10, "ЖОВТЕНЬ": 10, "ОКТ": 10, "PAZ": 10, "PAZDZIERNIK": 10,
    "NOV": 11, "NOVEMBER": 11, "ЛИС": 11, "ЛИСТОПАД": 11, "НОЯ": 11, "LIS": 11, "LISTOPAD": 11,
    "DEC": 12, "DECEMBER": 12, "ГРУ": 12, "ГРУДЕНЬ": 12, "ДЕК": 12, "GRU": 12, "GRUDZIEN": 12,
}

PLACE_LEADING_WORD_RE = re.compile(
    r"^(с\.?|село|дер\.?|деревня|д\.|місто|м\.|містечко|пос\.?|хут\.?|хутір|"
    r"village|town|city|miasteczko|wies|wieś)\s+",
    re.IGNORECASE,
)
PLACE_ADMIN_WORD_RE = re.compile(
    r"\b(пов\.?|повіт|повіту|уезд|уезда|pow\.?|powiat|губ\.?|губернія|губернии|"
    r"gubernia|вол\.?|волость|волості|парафія|парафии|parafia|parish|район|область|county)\b",
    re.IGNORECASE,
)


@dataclass
class Citation:
    source_ref: Optional[str] = None
    source_title: Optional[str] = None
    page: Optional[str] = None
    quality: Optional[str] = None
    citation_date_raw: Optional[str] = None
    text: Optional[str] = None
    note: Optional[str] = None


@dataclass
class Event:
    tag: str
    value: Optional[str] = None
    date_raw: Optional[str] = None
    date_from: Optional[str] = None
    date_to: Optional[str] = None
    date_precision: str = ""
    date_modifier: str = ""
    date_status: str = ""
    year_from: Optional[int] = None
    year_to: Optional[int] = None
    place_raw: Optional[str] = None
    note: Optional[str] = None
    event_type: Optional[str] = None
    related_xref: Optional[str] = None
    derived: bool = False
    citations: List[Citation] = field(default_factory=list)


@dataclass(frozen=True)
class NormalizedDate:
    date_from: Optional[str]
    date_to: Optional[str]
    year_from: Optional[int]
    year_to: Optional[int]
    precision: str
    modifier: str
    status: str


@dataclass
class Indi:
    xref: str
    names: List[str] = field(default_factory=list)
    sex: Optional[str] = None
    famc: List[str] = field(default_factory=list)
    fams: List[str] = field(default_factory=list)
    events: List[Event] = field(default_factory=list)


@dataclass
class Fam:
    xref: str
    husb: Optional[str] = None
    wife: Optional[str] = None
    chil: List[str] = field(default_factory=list)
    events: List[Event] = field(default_factory=list)


@dataclass
class SourceRecord:
    xref: str
    title: Optional[str] = None
    abbreviation: Optional[str] = None
    author: Optional[str] = None
    text: Optional[str] = None
    note: Optional[str] = None


@dataclass
class Gedcom:
    indis: Dict[str, Indi]
    fams: Dict[str, Fam]
    sources: Dict[str, SourceRecord]
    unresolved_refs: List[Dict[str, str]]


@dataclass(frozen=True)
class NormalizedName:
    raw_name: str
    full_name: str
    given_name: str
    patronymic: str
    surname: str
    maiden_surname: str
    married_surname: str
    title_prefix: str
    suffix: str
    script_code: str
    language_hint: str
    given_name_normalized: str
    patronymic_normalized: str
    surname_normalized: str
    full_name_normalized: str
    name_tokens: str
    variant_explanation: str
    normalization_confidence: float
    parser_status: str


def norm_space(value: Optional[str]) -> str:
    if value is None:
        return ""
    return re.sub(r"\s+", " ", value.strip())


def strip_diacritics(value: str) -> str:
    value = unicodedata.normalize("NFKD", value)
    return "".join(ch for ch in value if not unicodedata.combining(ch))


def norm_text(value: Optional[str]) -> str:
    return strip_diacritics(norm_space(value)).lower()


GIVEN_NAME_VARIANTS = {norm_text(variant): canonical for variant, canonical in GIVEN_NAME_VARIANT_PAIRS}
SURNAME_VARIANTS = {norm_text(variant): canonical for variant, canonical in SURNAME_VARIANT_PAIRS}


def _year_range_from_dates(date_from: Optional[str], date_to: Optional[str]) -> Tuple[Optional[int], Optional[int]]:
    year_from = int(date_from[:4]) if date_from else None
    year_to = int(date_to[:4]) if date_to else None
    return year_from, year_to


def _iso_date(year: int, month: int = 1, day: int = 1) -> str:
    return date(year, month, day).isoformat()


def _last_day_iso(year: int, month: int = 12) -> str:
    return date(year, month, calendar.monthrange(year, month)[1]).isoformat()


def _parse_single_date(value: str) -> Tuple[Optional[str], Optional[str], str]:
    value = norm_space(value)
    if not value:
        return None, None, "EMPTY"

    cleaned = re.sub(r"[/.,]", " ", strip_diacritics(value).upper())
    cleaned = norm_space(cleaned)
    tokens = cleaned.split()
    year_index = next((idx for idx in range(len(tokens) - 1, -1, -1) if re.fullmatch(r"\d{3,4}", tokens[idx])), None)
    if year_index is None:
        return None, None, "UNPARSED"

    year = int(tokens[year_index])
    if year < 1 or year > 9999:
        return None, None, "UNPARSED"

    month = None
    day = None
    if year_index >= 1:
        month = MONTH_ALIASES.get(tokens[year_index - 1])
    if month and year_index >= 2 and re.fullmatch(r"\d{1,2}", tokens[year_index - 2]):
        day = int(tokens[year_index - 2])

    if month and day:
        try:
            iso = _iso_date(year, month, day)
        except ValueError:
            return None, None, "UNPARSED"
        return iso, iso, "DAY"

    if month:
        return _iso_date(year, month, 1), _last_day_iso(year, month), "MONTH"

    return _iso_date(year, 1, 1), _last_day_iso(year, 12), "YEAR"


def parse_gedcom_date(date_raw: Optional[str]) -> NormalizedDate:
    if not date_raw:
        return NormalizedDate(None, None, None, None, "", "", "EMPTY")

    s = norm_space(date_raw).upper()
    s_key = strip_diacritics(s)
    modifier = ""

    def finish(date_from: Optional[str], date_to: Optional[str], precision: str, status: str = "OK") -> NormalizedDate:
        year_from, year_to = _year_range_from_dates(date_from, date_to)
        return NormalizedDate(date_from, date_to, year_from, year_to, precision, modifier, status)

    if s.startswith("BET ") and " AND " in s:
        modifier = "BET"
        left, right = s[4:].split(" AND ", 1)
        date_from, _, precision_from = _parse_single_date(left)
        right_key = strip_diacritics(norm_space(right).upper())
        if right_key.startswith(("AFT ", "AFTER ", "ПІСЛЯ ", "ПИСЛЯ ", "ПОСЛЕ ")):
            date_to, precision_to = None, "OPEN"
        else:
            _, date_to, precision_to = _parse_single_date(right)
        precision = precision_from if precision_from == precision_to else "RANGE"
        return finish(date_from, date_to, precision, "OK" if date_from or date_to else "UNPARSED")

    if s.startswith("FROM ") and " TO " in s:
        modifier = "FROM_TO"
        left, right = s[5:].split(" TO ", 1)
        date_from, _, precision_from = _parse_single_date(left)
        _, date_to, precision_to = _parse_single_date(right)
        precision = precision_from if precision_from == precision_to else "RANGE"
        return finish(date_from, date_to, precision, "OK" if date_from or date_to else "UNPARSED")

    for prefix, mod in (("BEF ", "BEF"), ("BEFORE ", "BEF"), ("TO ", "TO")):
        if s.startswith(prefix):
            modifier = mod
            _, date_to, precision = _parse_single_date(s[len(prefix):])
            return finish(None, date_to, precision, "OK" if date_to else "UNPARSED")

    for prefix, mod in (("ДО ", "BEF"),):
        if s_key.startswith(prefix):
            modifier = mod
            _, date_to, precision = _parse_single_date(s_key[len(prefix):])
            return finish(None, date_to, precision, "OK" if date_to else "UNPARSED")

    for prefix, mod in (("AFT ", "AFT"), ("AFTER ", "AFT"), ("FROM ", "FROM")):
        if s.startswith(prefix):
            modifier = mod
            date_from, _, precision = _parse_single_date(s[len(prefix):])
            return finish(date_from, None, precision, "OK" if date_from else "UNPARSED")

    for prefix, mod in (("ПІСЛЯ ", "AFT"), ("ПИСЛЯ ", "AFT"), ("ПОСЛЕ ", "AFT")):
        if s_key.startswith(prefix):
            modifier = mod
            date_from, _, precision = _parse_single_date(s_key[len(prefix):])
            return finish(date_from, None, precision, "OK" if date_from else "UNPARSED")

    for prefix, mod in (
        ("ABT ", "ABT"),
        ("ABOUT ", "ABT"),
        ("CA ", "ABT"),
        ("CIRCA ", "ABT"),
        ("EST ", "EST"),
        ("ESTIMATED ", "EST"),
        ("CAL ", "CAL"),
        ("CALCULATED ", "CAL"),
    ):
        if s.startswith(prefix):
            modifier = mod
            date_from, date_to, precision = _parse_single_date(s[len(prefix):])
            return finish(date_from, date_to, precision, "OK" if date_from or date_to else "UNPARSED")

    for prefix, mod in (("БЛИЗЬКО ", "ABT"), ("ОКОЛО ", "ABT"), ("ПРИБЛИЗНО ", "ABT"), ("ПРИБЛ ", "ABT")):
        if s_key.startswith(prefix):
            modifier = mod
            date_from, date_to, precision = _parse_single_date(s_key[len(prefix):])
            return finish(date_from, date_to, precision, "OK" if date_from or date_to else "UNPARSED")

    date_from, date_to, precision = _parse_single_date(s)
    return finish(date_from, date_to, precision, "OK" if date_from or date_to else "UNPARSED")


def parse_date_year_range(date_raw: Optional[str]) -> Tuple[Optional[int], Optional[int]]:
    parsed = parse_gedcom_date(date_raw)
    return parsed.year_from, parsed.year_to


def normalize_place_key(value: Optional[str]) -> str:
    raw = norm_space(value)
    if not raw:
        return ""

    components = re.split(r"\s*[,;|]\s*", raw)
    normalized_parts: List[str] = []
    for component in components:
        part = strip_diacritics(norm_space(component)).lower()
        part = PLACE_LEADING_WORD_RE.sub("", part)
        part = PLACE_ADMIN_WORD_RE.sub("", part)
        part = re.sub(r"[\[\](){}]", " ", part)
        part = re.sub(r"[^0-9a-zа-яіїєґąćęłńóśźż]+", " ", part, flags=re.IGNORECASE)
        part = norm_space(part)
        if part:
            normalized_parts.append(part)

    return ", ".join(normalized_parts)


def set_event_date(event: Event, value: Optional[str]) -> None:
    parsed = parse_gedcom_date(value)
    event.date_raw = norm_space(value) or None
    event.date_from = parsed.date_from
    event.date_to = parsed.date_to
    event.date_precision = parsed.precision
    event.date_modifier = parsed.modifier
    event.date_status = parsed.status
    event.year_from = parsed.year_from
    event.year_to = parsed.year_to


def date_warning_kind(event: Event) -> str:
    if not event.date_raw:
        return ""
    if event.date_status != "OK":
        return "UNPARSED"
    if event.date_modifier in {"ABT", "EST", "CAL"}:
        return "APPROXIMATE"
    if event.date_modifier in {"BEF", "AFT", "FROM", "TO"}:
        return "OPEN_BOUND"
    if event.date_modifier in {"BET", "FROM_TO"}:
        return "RANGE"
    return ""


def date_warning_message(event: Event) -> str:
    kind = date_warning_kind(event)
    if kind == "UNPARSED":
        return "Date could not be parsed into a year/date range."
    if kind == "APPROXIMATE":
        return "Approximate/calculated date; do not treat as exact identity evidence."
    if kind == "OPEN_BOUND":
        return "Open-ended date bound; only one side of the date range is known."
    if kind == "RANGE":
        return "Date range; usable for overlap checks but not exact identity evidence."
    return ""


def read_text_guess_encoding(path: Path) -> str:
    data = path.read_bytes()
    if data.startswith(b"\xef\xbb\xbf"):
        return data.decode("utf-8-sig", errors="replace")
    if data.startswith(b"\xff\xfe") or data.startswith(b"\xfe\xff"):
        return data.decode("utf-16", errors="replace")
    return data.decode("utf-8", errors="replace")


@dataclass(frozen=True)
class TreeUuidScope:
    tree_id: uuid.UUID
    legacy_ids: bool = True


def person_uuid(xref: str, scope: Optional[TreeUuidScope] = None) -> str:
    # scope=None (or legacy_ids) keeps the legacy bare-xref key used by the
    # analysis subcommands. Tree-scoped keys are used only when an explicit
    # non-legacy scope is supplied.
    if scope is None or scope.legacy_ids:
        key = xref
    else:
        key = f"TREE:{scope.tree_id}:INDI:{xref}"
    return str(uuid.uuid5(NAMESPACE_UUID, key))


def family_uuid(xref: str, scope: Optional[TreeUuidScope] = None) -> str:
    if scope is None or scope.legacy_ids:
        key = "FAM:" + xref
    else:
        key = f"TREE:{scope.tree_id}:FAM:{xref}"
    return str(uuid.uuid5(NAMESPACE_UUID, key))


def script_of(value: str) -> str:
    value = value or ""
    has_cyr = bool(_CYR_RE.search(value))
    has_lat = bool(_LAT_RE.search(value))
    if has_cyr and has_lat:
        return "MIXED"
    if has_cyr:
        return "CYR"
    if has_lat:
        return "LAT"
    return "OTHER"


def split_alts(value: str) -> List[str]:
    value = norm_space(value)
    if not value:
        return []
    parts = [norm_space(part) for part in value.split("/") if norm_space(part)]
    return parts if parts else [value]


def split_name_guess(raw_full: str) -> Tuple[str, str]:
    raw_full = norm_space(raw_full)
    if not raw_full:
        return "", ""
    parts = raw_full.split()
    if len(parts) == 1:
        return raw_full, ""
    return " ".join(parts[:-1]), parts[-1]


def parse_gedcom_name_parts(raw: str) -> Tuple[str, str, str]:
    raw = norm_space(raw or "")
    if not raw:
        return "", "", ""

    # GEDCOM marks surname with slashes, but MyHeritage exports can also use
    # slashes for multilingual alternatives: "Іван/Jan /Семашко/Siemaszko/".
    # Treat the last " /" marker as the surname boundary, then split variants
    # inside both sides later.
    surname_marker = raw.rfind(" /")
    if surname_marker >= 0:
        given = norm_space(raw[:surname_marker])
        surname = norm_space(raw[surname_marker + 2:].strip("/"))
        return given, surname, norm_space(f"{given} {surname}")

    given, surname = split_name_guess(raw)
    return given, surname, norm_space(f"{given} {surname}")


def build_name_variants(raw_name: str) -> List[Dict[str, str]]:
    given_part, surname_part, fallback_full = parse_gedcom_name_parts(raw_name)
    given_alts = split_alts(given_part) if given_part else [""]
    surname_alts = split_alts(surname_part) if surname_part else [""]

    candidate_pairs: List[Tuple[str, str, str, str, str]] = []
    has_same_script_pair = False
    for given in given_alts:
        for surname in surname_alts:
            given_script = script_of(given)
            surname_script = script_of(surname) if surname else given_script
            same_script = given_script == surname_script or not surname
            has_same_script_pair = has_same_script_pair or same_script
            script_code = given_script if same_script else "MIXED"
            candidate_pairs.append((given, surname, given_script, surname_script, script_code))

    out: List[Dict[str, str]] = []
    seen: Set[Tuple[str, str]] = set()
    for given, surname, given_script, surname_script, script_code in candidate_pairs:
        if has_same_script_pair and given_script != surname_script and surname:
            continue
        full = norm_space(f"{given} {surname}") or fallback_full
        if not full:
            continue
        key = (script_code, norm_text(full))
        if key in seen:
            continue
        seen.add(key)
        out.append({
            "ScriptCode": script_code,
            "Given": given,
            "Surname": surname,
            "FullName": full,
            "FullNameNormalized": norm_text(full),
        })
    return out


def pick_primary_variant(variants: List[Dict[str, str]], fallback: str) -> Dict[str, str]:
    for script in ("CYR", "LAT", "OTHER"):
        for variant in variants:
            if variant["ScriptCode"] == script:
                return variant
    full = clean_gedcom_name(fallback) or fallback
    return {
        "ScriptCode": script_of(full),
        "Given": "",
        "Surname": "",
        "FullName": full,
        "FullNameNormalized": norm_text(full),
    }


def language_hint(value: str) -> str:
    script = script_of(value)
    if script == "CYR":
        return "uk-ru"
    if script == "LAT":
        return "pl-la"
    if script == "MIXED":
        return "mixed"
    return "unknown"


def split_title_prefix(value: str) -> Tuple[str, str]:
    value = norm_space(value)
    if not value:
        return "", ""
    parts = value.split()
    title_parts: List[str] = []
    while parts and norm_text(parts[0]) in {norm_text(title) for title in TITLE_PREFIXES}:
        title_parts.append(parts.pop(0))
    return norm_space(" ".join(title_parts)), norm_space(" ".join(parts))


def split_patronymic(value: str) -> Tuple[str, str]:
    value = norm_space(value)
    if not value:
        return "", ""
    parts = value.split()
    if len(parts) >= 2 and _PATRONYMIC_RE.search(parts[-1]):
        return norm_space(" ".join(parts[:-1])), parts[-1]
    return value, ""


def normalize_component(rule_type: str, value: str) -> Tuple[str, str]:
    value = norm_space(value)
    if not value:
        return "", ""

    raw_norm = norm_text(value)
    if rule_type == "given_name":
        canonical = GIVEN_NAME_VARIANTS.get(raw_norm)
    elif rule_type == "surname":
        canonical = SURNAME_VARIANTS.get(raw_norm)
    else:
        canonical = None

    if canonical:
        return norm_text(canonical), f"{rule_type}:{value}->{canonical}"
    return raw_norm, ""


def build_name_tokens(*values: str) -> str:
    tokens: List[str] = []
    seen: Set[str] = set()
    for value in values:
        for token in norm_text(value).split():
            if token and token not in seen:
                seen.add(token)
                tokens.append(token)
    return " ".join(tokens)


def normalize_name_for_preflight(raw_name: str, variant: Dict[str, str]) -> NormalizedName:
    raw_name = norm_space(raw_name)
    given = norm_space(variant.get("Given"))
    surname = norm_space(variant.get("Surname"))
    full_name = norm_space(variant.get("FullName") or f"{given} {surname}" or raw_name)
    title_prefix, given_without_title = split_title_prefix(given)
    given_without_patronymic, patronymic = split_patronymic(given_without_title)

    given_norm, given_explanation = normalize_component("given_name", given_without_patronymic)
    patronymic_norm = norm_text(patronymic)
    surname_norm, surname_explanation = normalize_component("surname", surname)
    canonical_full = norm_space(
        " ".join(part for part in (given_norm, patronymic_norm, surname_norm) if part)
    )
    raw_full_norm = norm_text(full_name)
    full_norm = canonical_full or raw_full_norm

    explanations = [item for item in (given_explanation, surname_explanation) if item]
    confidence = 1.0
    if not given_without_patronymic:
        confidence -= 0.25
    if not surname:
        confidence -= 0.20
    if title_prefix:
        confidence -= 0.05
    if _UNCERTAINTY_RE.search(raw_name):
        confidence -= 0.30
        explanations.append("uncertainty-marker")
    if script_of(full_name) == "MIXED":
        confidence -= 0.10
        explanations.append("mixed-script")
    if raw_name.startswith("Unnamed "):
        confidence = min(confidence, 0.25)
        explanations.append("generated-unnamed-label")
    confidence = max(0.0, min(1.0, confidence))

    if not full_name:
        status = "MISSING_NAME"
    elif raw_name.startswith("Unnamed "):
        status = "UNNAMED"
    elif _UNCERTAINTY_RE.search(raw_name):
        status = "AMBIGUOUS"
    elif confidence < 0.75:
        status = "LOW_CONFIDENCE"
    else:
        status = "OK"

    return NormalizedName(
        raw_name=raw_name,
        full_name=full_name,
        given_name=given_without_patronymic,
        patronymic=patronymic,
        surname=surname,
        maiden_surname="",
        married_surname="",
        title_prefix=title_prefix,
        suffix="",
        script_code=variant.get("ScriptCode") or script_of(full_name),
        language_hint=language_hint(full_name),
        given_name_normalized=given_norm,
        patronymic_normalized=patronymic_norm,
        surname_normalized=surname_norm,
        full_name_normalized=full_norm,
        name_tokens=build_name_tokens(raw_name, given_norm, patronymic_norm, surname_norm, raw_full_norm),
        variant_explanation="; ".join(explanations),
        normalization_confidence=round(confidence, 4),
        parser_status=status,
    )


def normalized_name_to_row(
    batch_id: str,
    row_number: int,
    source_name_row_number: int,
    tree_person_id: str,
    name_type: str,
    normalized: NormalizedName,
) -> Dict[str, object]:
    return {
        "ImportBatchId": batch_id,
        "RowNumber": row_number,
        "SourceNameRowNumber": source_name_row_number,
        "TreePersonId": tree_person_id,
        "RawName": normalized.raw_name,
        "NameType": name_type,
        "ScriptCode": normalized.script_code,
        "GivenName": normalized.given_name,
        "Patronymic": normalized.patronymic,
        "Surname": normalized.surname,
        "MaidenSurname": normalized.maiden_surname,
        "MarriedSurname": normalized.married_surname,
        "TitlePrefix": normalized.title_prefix,
        "Suffix": normalized.suffix,
        "LanguageHint": normalized.language_hint,
        "GivenNameNormalized": normalized.given_name_normalized,
        "PatronymicNormalized": normalized.patronymic_normalized,
        "SurnameNormalized": normalized.surname_normalized,
        "FullNameNormalized": normalized.full_name_normalized,
        "NameTokens": normalized.name_tokens,
        "VariantExplanation": normalized.variant_explanation,
        "NormalizationConfidence": f"{normalized.normalization_confidence:.4f}",
        "ParserStatus": normalized.parser_status,
    }


def has_real_name(indi: Optional[Indi]) -> bool:
    if not indi or not indi.names:
        return False
    return bool(clean_gedcom_name(indi.names[0]))


def build_unnamed_person_labels(ged: Gedcom) -> Dict[str, str]:
    labels: Dict[str, str] = {}
    for xref, indi in ged.indis.items():
        if has_real_name(indi):
            continue

        spouse_name = ""
        child_name = ""
        for fam in ged.fams.values():
            if fam.husb == xref and fam.wife in ged.indis:
                spouse_name = display_name(ged.indis[fam.wife])
            elif fam.wife == xref and fam.husb in ged.indis:
                spouse_name = display_name(ged.indis[fam.husb])
            if xref in (fam.husb, fam.wife):
                for child in fam.chil:
                    if child in ged.indis and has_real_name(ged.indis[child]):
                        child_name = display_name(ged.indis[child])
                        break
            if spouse_name or child_name:
                break

        if spouse_name:
            role = "wife" if indi.sex == "F" else ("husband" if indi.sex == "M" else "spouse")
            labels[xref] = f"Unnamed {role} of {spouse_name}"
        elif child_name:
            labels[xref] = f"Unnamed parent of {child_name}"
        else:
            labels[xref] = f"Unnamed person {xref}"
    return labels


def display_name(indi: Optional[Indi]) -> str:
    if not indi:
        return ""
    if not indi.names:
        return indi.xref
    return clean_gedcom_name(indi.names[0])


def clean_gedcom_name(raw: str) -> str:
    return norm_space(raw.replace("/", " "))


def event_value_or_none(tag: str, value: str) -> Optional[str]:
    value = norm_space(value)
    if not value:
        return None
    # GEDCOM uses "Y" on standard event tags to mean "the event happened".
    # It is not descriptive event content and should not create duplicate rows
    # when matching older imports that stored the same event with NULL value.
    if value.upper() == "Y" and tag in EVENT_LABELS:
        return None
    return value


def append_field(event: Event, field_name: str, value: str, continuation: bool) -> None:
    current = getattr(event, field_name) or ""
    sep = "\n" if continuation and current else ""
    setattr(event, field_name, current + sep + value)
    if field_name == "date_raw":
        set_event_date(event, event.date_raw)


def append_dataclass_field(obj: object, field_name: str, value: str, continuation: bool) -> None:
    current = getattr(obj, field_name) or ""
    sep = "\n" if continuation and current else ""
    setattr(obj, field_name, current + sep + value)


def parse_gedcom(path: Path) -> Gedcom:
    text = read_text_guess_encoding(path)
    indis: Dict[str, Indi] = {}
    fams: Dict[str, Fam] = {}
    sources: Dict[str, SourceRecord] = {}

    current_indi: Optional[Indi] = None
    current_fam: Optional[Fam] = None
    current_source: Optional[SourceRecord] = None
    current_event: Optional[Event] = None
    current_citation: Optional[Citation] = None
    current_context: Optional[str] = None
    last_event_field: Optional[str] = None
    last_citation_field: Optional[str] = None
    last_source_field: Optional[str] = None

    def close_event() -> None:
        nonlocal current_event, current_citation, last_event_field, last_citation_field
        if current_event is None:
            return
        if current_event.tag.startswith("_") and not current_event.place_raw:
            current_event = None
            current_citation = None
            last_event_field = None
            last_citation_field = None
            return
        if current_context == "INDI" and current_indi is not None:
            current_indi.events.append(current_event)
        elif current_context == "FAM" and current_fam is not None:
            current_fam.events.append(current_event)
        current_event = None
        current_citation = None
        last_event_field = None
        last_citation_field = None

    for raw_line in text.splitlines():
        line = raw_line.rstrip("\r\n")
        if not line.strip():
            continue

        match = re.match(r"^(\d+)\s+(?:(@[^@]+@)\s+)?([A-Z0-9_]+)(?:\s+(.*))?$", line)
        if not match:
            continue

        level = int(match.group(1))
        xref = match.group(2)
        tag = match.group(3)
        value = match.group(4) if match.group(4) is not None else ""

        if level == 0:
            close_event()
            current_indi = None
            current_fam = None
            current_source = None
            current_citation = None
            current_context = None
            last_source_field = None
            if tag == "INDI" and xref:
                current_context = "INDI"
                current_indi = indis.setdefault(xref, Indi(xref=xref))
            elif tag == "FAM" and xref:
                current_context = "FAM"
                current_fam = fams.setdefault(xref, Fam(xref=xref))
            elif tag == "SOUR" and xref:
                current_context = "SOUR"
                current_source = sources.setdefault(xref, SourceRecord(xref=xref))
            continue

        if tag in ("CONC", "CONT") and current_citation is not None and last_citation_field:
            append_dataclass_field(current_citation, last_citation_field, value, continuation=(tag == "CONT"))
            continue

        if tag in ("CONC", "CONT") and current_event is not None and last_event_field:
            append_field(current_event, last_event_field, value, continuation=(tag == "CONT"))
            continue

        if tag in ("CONC", "CONT") and current_source is not None and last_source_field:
            append_dataclass_field(current_source, last_source_field, value, continuation=(tag == "CONT"))
            continue

        if current_context == "INDI" and current_indi is not None:
            if level == 1:
                close_event()
                last_event_field = None
                last_citation_field = None
                if tag == "NAME":
                    current_indi.names.append(value)
                elif tag == "SEX":
                    sex = value.strip().upper()
                    current_indi.sex = sex[0] if sex else None
                elif tag == "FAMC" and value:
                    current_indi.famc.append(value.strip())
                elif tag == "FAMS" and value:
                    current_indi.fams.append(value.strip())
                elif tag not in STRUCTURAL_INDI_TAGS:
                    current_event = Event(tag=tag, value=event_value_or_none(tag, value))
                    if tag == "NOTE" and value:
                        current_event.note = value
                        last_event_field = "note"
                continue

            if level >= 2 and current_event is not None:
                if tag == "SOUR":
                    current_citation = Citation(source_ref=norm_space(value) or None)
                    current_event.citations.append(current_citation)
                    last_citation_field = None
                    last_event_field = None
                    continue
                if level > 2 and current_citation is not None:
                    if tag == "PAGE":
                        append_dataclass_field(current_citation, "page", value, continuation=False)
                        last_citation_field = "page"
                    elif tag == "QUAY":
                        current_citation.quality = norm_space(value) or None
                        last_citation_field = None
                    elif tag == "TEXT":
                        append_dataclass_field(current_citation, "text", value, continuation=False)
                        last_citation_field = "text"
                    elif tag == "NOTE":
                        append_dataclass_field(current_citation, "note", value, continuation=False)
                        last_citation_field = "note"
                    elif tag == "DATE":
                        current_citation.citation_date_raw = norm_space(value) or None
                        last_citation_field = None
                    elif tag == "TITL":
                        append_dataclass_field(current_citation, "source_title", value, continuation=False)
                        last_citation_field = "source_title"
                    else:
                        last_citation_field = None
                    continue
                current_citation = None
                last_citation_field = None
                if tag == "DATE":
                    set_event_date(current_event, value)
                    last_event_field = "date_raw"
                elif tag == "PLAC":
                    current_event.place_raw = value
                    last_event_field = "place_raw"
                elif tag == "NOTE":
                    append_field(current_event, "note", value, continuation=False)
                    last_event_field = "note"
                elif tag == "TYPE":
                    current_event.event_type = norm_space(value) or None
                    last_event_field = None
                elif tag == "ADDR":
                    if value:
                        current_event.place_raw = norm_space(
                            f"{current_event.place_raw or ''} {value}"
                        )
                    last_event_field = "place_raw"
                else:
                    last_event_field = None

        elif current_context == "FAM" and current_fam is not None:
            if level == 1:
                close_event()
                last_event_field = None
                last_citation_field = None
                if tag == "HUSB" and value:
                    current_fam.husb = value.strip()
                elif tag == "WIFE" and value:
                    current_fam.wife = value.strip()
                elif tag == "CHIL" and value:
                    current_fam.chil.append(value.strip())
                elif tag not in STRUCTURAL_FAM_TAGS:
                    current_event = Event(tag=tag, value=event_value_or_none(tag, value))
                continue

            if level >= 2 and current_event is not None:
                if tag == "SOUR":
                    current_citation = Citation(source_ref=norm_space(value) or None)
                    current_event.citations.append(current_citation)
                    last_citation_field = None
                    last_event_field = None
                    continue
                if level > 2 and current_citation is not None:
                    if tag == "PAGE":
                        append_dataclass_field(current_citation, "page", value, continuation=False)
                        last_citation_field = "page"
                    elif tag == "QUAY":
                        current_citation.quality = norm_space(value) or None
                        last_citation_field = None
                    elif tag == "TEXT":
                        append_dataclass_field(current_citation, "text", value, continuation=False)
                        last_citation_field = "text"
                    elif tag == "NOTE":
                        append_dataclass_field(current_citation, "note", value, continuation=False)
                        last_citation_field = "note"
                    elif tag == "DATE":
                        current_citation.citation_date_raw = norm_space(value) or None
                        last_citation_field = None
                    elif tag == "TITL":
                        append_dataclass_field(current_citation, "source_title", value, continuation=False)
                        last_citation_field = "source_title"
                    else:
                        last_citation_field = None
                    continue
                current_citation = None
                last_citation_field = None
                if tag == "DATE":
                    set_event_date(current_event, value)
                    last_event_field = "date_raw"
                elif tag == "PLAC":
                    current_event.place_raw = value
                    last_event_field = "place_raw"
                elif tag == "NOTE":
                    append_field(current_event, "note", value, continuation=False)
                    last_event_field = "note"
                elif tag == "TYPE":
                    current_event.event_type = norm_space(value) or None
                    last_event_field = None
                elif tag == "ADDR":
                    if value:
                        current_event.place_raw = norm_space(
                            f"{current_event.place_raw or ''} {value}"
                        )
                    last_event_field = "place_raw"
                else:
                    last_event_field = None

        elif current_context == "SOUR" and current_source is not None:
            if level == 1:
                if tag == "TITL":
                    append_dataclass_field(current_source, "title", value, continuation=False)
                    last_source_field = "title"
                elif tag == "ABBR":
                    append_dataclass_field(current_source, "abbreviation", value, continuation=False)
                    last_source_field = "abbreviation"
                elif tag == "AUTH":
                    append_dataclass_field(current_source, "author", value, continuation=False)
                    last_source_field = "author"
                elif tag == "TEXT":
                    append_dataclass_field(current_source, "text", value, continuation=False)
                    last_source_field = "text"
                elif tag == "NOTE":
                    append_dataclass_field(current_source, "note", value, continuation=False)
                    last_source_field = "note"
                else:
                    last_source_field = None

    close_event()

    unresolved: List[Dict[str, str]] = []
    for fam in fams.values():
        for role, target in (("HUSB", fam.husb), ("WIFE", fam.wife)):
            if target and target not in indis:
                unresolved.append({"type": "FAM_PERSON_REF", "source": fam.xref, "role": role, "target": target})
        for child in fam.chil:
            if child not in indis:
                unresolved.append({"type": "FAM_CHILD_REF", "source": fam.xref, "role": "CHIL", "target": child})

    return Gedcom(indis=indis, fams=fams, sources=sources, unresolved_refs=unresolved)


def iter_person_events(ged: Gedcom, include_derived: bool = True) -> Iterable[Tuple[str, Event]]:
    for xref, indi in ged.indis.items():
        for event in indi.events:
            yield xref, event

    for fam in ged.fams.values():
        spouses = [xref for xref in (fam.husb, fam.wife) if xref in ged.indis]
        for event in fam.events:
            for spouse in spouses:
                derived = Event(
                    tag=event.tag,
                    value=event.value,
                    date_raw=event.date_raw,
                    date_from=event.date_from,
                    date_to=event.date_to,
                    date_precision=event.date_precision,
                    date_modifier=event.date_modifier,
                    date_status=event.date_status,
                    year_from=event.year_from,
                    year_to=event.year_to,
                    place_raw=event.place_raw,
                    note=event.note,
                    event_type=event.event_type,
                    related_xref=fam.xref,
                    derived=True,
                    citations=list(event.citations),
                )
                yield spouse, derived

    if include_derived:
        birth_by_child: Dict[str, Event] = {}
        for xref, indi in ged.indis.items():
            birth = first_event(indi, "BIRT")
            if birth:
                birth_by_child[xref] = birth
        for fam in ged.fams.values():
            parents = [xref for xref in (fam.husb, fam.wife) if xref in ged.indis]
            for child in fam.chil:
                child_birth = birth_by_child.get(child)
                if not child_birth:
                    continue
                for parent in parents:
                    yield parent, Event(
                        tag="CHILD_BIRTH",
                        value=display_name(ged.indis.get(child)),
                        date_raw=child_birth.date_raw,
                        date_from=child_birth.date_from,
                        date_to=child_birth.date_to,
                        date_precision=child_birth.date_precision,
                        date_modifier=child_birth.date_modifier,
                        date_status=child_birth.date_status,
                        year_from=child_birth.year_from,
                        year_to=child_birth.year_to,
                        place_raw=child_birth.place_raw,
                        related_xref=child,
                        derived=True,
                        citations=list(child_birth.citations),
                    )


def first_event(indi: Indi, tag: str) -> Optional[Event]:
    for event in indi.events:
        if event.tag == tag:
            return event
    return None


def write_tsv(path: Path, headers: List[str], rows: Iterable[Dict[str, object]], encoding: str = "utf-8") -> int:
    path.parent.mkdir(parents=True, exist_ok=True)
    count = 0
    with path.open("w", encoding=encoding, newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=headers, delimiter="\t", lineterminator="\n")
        writer.writeheader()
        for row in rows:
            writer.writerow({key: normalize_tsv_value(row.get(key)) for key in headers})
            count += 1
    return count


def normalize_tsv_value(value: object) -> object:
    if value is None:
        return ""
    if isinstance(value, str):
        return value.replace("\r", " ").replace("\n", " ")
    return value


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def bit_or_null(value: Optional[bool]) -> Optional[int]:
    if value is None:
        return None
    return 1 if value else 0


def guess_is_living(birth_year_to: Optional[int], death_year_from: Optional[int]) -> Optional[bool]:
    if death_year_from is not None:
        return False
    if birth_year_to is None:
        return None
    return True if birth_year_to >= 1906 else None


def event_identity_key(xref: str, event: Event, ordinal: int) -> str:
    raw = "|".join([
        xref,
        event.tag or "",
        event.value or "",
        event.date_raw or "",
        str(event.year_from or ""),
        str(event.year_to or ""),
        event.place_raw or "",
        event.related_xref or "",
        "1" if event.derived else "0",
        str(ordinal),
    ])
    return hashlib.sha1(raw.encode("utf-8")).hexdigest()


def source_title_for_citation(ged: Gedcom, citation: Citation) -> Optional[str]:
    if citation.source_title:
        return norm_space(citation.source_title)
    if citation.source_ref:
        source = ged.sources.get(citation.source_ref)
        if source:
            return norm_space(source.title or source.abbreviation or source.author or "") or None
    return None


def build_graphs(ged: Gedcom) -> Tuple[DefaultDict[str, Set[str]], DefaultDict[str, Set[str]], DefaultDict[str, Set[str]]]:
    parents: DefaultDict[str, Set[str]] = defaultdict(set)
    children: DefaultDict[str, Set[str]] = defaultdict(set)
    spouses: DefaultDict[str, Set[str]] = defaultdict(set)

    for fam in ged.fams.values():
        family_parents = [xref for xref in (fam.husb, fam.wife) if xref in ged.indis]
        if len(family_parents) == 2:
            spouses[family_parents[0]].add(family_parents[1])
            spouses[family_parents[1]].add(family_parents[0])
        for child in fam.chil:
            if child not in ged.indis:
                continue
            for parent in family_parents:
                parents[child].add(parent)
                children[parent].add(child)

    return parents, children, spouses


def ancestors_of(root: str, parents: Dict[str, Set[str]]) -> Set[str]:
    seen = {root}
    queue = deque([root])
    while queue:
        current = queue.popleft()
        for nxt in parents.get(current, set()):
            if nxt not in seen:
                seen.add(nxt)
                queue.append(nxt)
    return seen


def descendants_of_many(roots: Iterable[str], children: Dict[str, Set[str]]) -> Set[str]:
    seen = set(roots)
    queue = deque(roots)
    while queue:
        current = queue.popleft()
        for nxt in children.get(current, set()):
            if nxt not in seen:
                seen.add(nxt)
                queue.append(nxt)
    return seen


def blood_relatives(root: str, parents: Dict[str, Set[str]], children: Dict[str, Set[str]]) -> Set[str]:
    # Do not use an undirected parent-child connected component: that incorrectly
    # makes spouses blood relatives through shared children. Blood relatives are
    # root's ancestors plus all descendants of those ancestors.
    ancestors = ancestors_of(root, parents)
    return descendants_of_many(ancestors, children)


def descendants_count(root: str, children: Dict[str, Set[str]]) -> int:
    seen: Set[str] = set()
    queue = deque(children.get(root, set()))
    while queue:
        current = queue.popleft()
        if current in seen:
            continue
        seen.add(current)
        queue.extend(children.get(current, set()) - seen)
    return len(seen)


def resolve_person(ged: Gedcom, token: str) -> str:
    token = token.strip()
    if token in ged.indis:
        return token

    matches: List[str] = []
    norm = norm_text(token)
    for xref, indi in ged.indis.items():
        names = indi.names or [xref]
        if any(norm in norm_text(name) for name in names):
            matches.append(xref)

    if not matches:
        raise SystemExit(f"Person not found: {token}")
    if len(matches) > 1:
        sample = ", ".join(f"{xref}:{display_name(ged.indis[xref])}" for xref in matches[:10])
        raise SystemExit(f"Ambiguous person '{token}'. Use xref. Matches: {sample}")
    return matches[0]


def event_to_row(ged: Gedcom, xref: str, event: Event) -> Dict[str, object]:
    return {
        "PersonXref": xref,
        "PersonUuid": person_uuid(xref),
        "PersonName": display_name(ged.indis.get(xref)),
        "EventTag": event.tag,
        "EventLabel": EVENT_LABELS.get(event.tag, event.tag),
        "EventType": event.event_type,
        "Value": event.value,
        "DateRaw": event.date_raw,
        "DateFrom": event.date_from,
        "DateTo": event.date_to,
        "DatePrecision": event.date_precision,
        "DateModifier": event.date_modifier,
        "DateStatus": event.date_status,
        "DateWarningKind": date_warning_kind(event),
        "YearFrom": event.year_from,
        "YearTo": event.year_to,
        "PlaceRaw": event.place_raw,
        "PlaceNormalized": normalize_place_key(event.place_raw),
        "RelatedXref": event.related_xref,
        "Derived": 1 if event.derived else 0,
        "Note": event.note,
    }


def run_inspect(args: argparse.Namespace) -> int:
    ged = parse_gedcom(Path(args.gedcom))
    out_dir = Path(args.out)
    out_dir.mkdir(parents=True, exist_ok=True)

    parents, children, spouses = build_graphs(ged)

    event_rows = [event_to_row(ged, xref, event) for xref, event in iter_person_events(ged)]
    place_event_rows = [row for row in event_rows if row["PlaceRaw"]]

    event_counter = Counter(row["EventTag"] for row in event_rows)
    place_counter = Counter(row["PlaceRaw"] for row in place_event_rows)
    person_place_counter: DefaultDict[str, Set[str]] = defaultdict(set)
    place_people: DefaultDict[str, Set[str]] = defaultdict(set)
    place_types: DefaultDict[str, Counter] = defaultdict(Counter)
    for row in place_event_rows:
        place = str(row["PlaceRaw"])
        person_place_counter[str(row["PersonXref"])].add(place)
        place_people[place].add(str(row["PersonXref"]))
        place_types[place][str(row["EventTag"])] += 1

    person_rows = []
    name_norm_to_xrefs: DefaultDict[str, Set[str]] = defaultdict(set)
    for xref, indi in sorted(ged.indis.items()):
        birth = first_event(indi, "BIRT")
        death = first_event(indi, "DEAT")
        primary_name = display_name(indi)
        name_norm_to_xrefs[norm_text(primary_name)].add(xref)
        person_rows.append({
            "Xref": xref,
            "Uuid": person_uuid(xref),
            "PrimaryName": primary_name,
            "Sex": indi.sex,
            "BirthYear": birth.year_from if birth else None,
            "BirthPlace": birth.place_raw if birth else None,
            "DeathYear": death.year_from if death else None,
            "DeathPlace": death.place_raw if death else None,
            "ParentCount": len(parents.get(xref, set())),
            "SpouseCount": len(spouses.get(xref, set())),
            "ChildCount": len(children.get(xref, set())),
            "FAMC": ",".join(indi.famc),
            "FAMS": ",".join(indi.fams),
            "EventCount": sum(1 for row in event_rows if row["PersonXref"] == xref),
            "PlaceLinkedEventCount": sum(1 for row in place_event_rows if row["PersonXref"] == xref),
            "DistinctPlaceCount": len(person_place_counter.get(xref, set())),
        })

    family_rows = []
    for xref, fam in sorted(ged.fams.items()):
        event_summary = ";".join(
            f"{ev.tag}:{ev.year_from or ''}:{ev.place_raw or ''}" for ev in fam.events
        )
        family_rows.append({
            "Xref": xref,
            "Uuid": family_uuid(xref),
            "HusbandXref": fam.husb,
            "HusbandName": display_name(ged.indis.get(fam.husb or "")),
            "WifeXref": fam.wife,
            "WifeName": display_name(ged.indis.get(fam.wife or "")),
            "ChildCount": len(fam.chil),
            "Children": ",".join(fam.chil),
            "Events": event_summary,
        })

    place_rows = []
    for place, count in place_counter.most_common():
        people = sorted(place_people[place])
        type_summary = ",".join(f"{tag}:{cnt}" for tag, cnt in place_types[place].most_common())
        place_rows.append({
            "PlaceRaw": place,
            "PlaceNormalized": normalize_place_key(place),
            "EventCount": count,
            "PersonCount": len(people),
            "EventTypes": type_summary,
            "SamplePeople": "; ".join(f"{xref}:{display_name(ged.indis.get(xref))}" for xref in people[:8]),
        })

    collision_rows = []
    for name_norm, xrefs in sorted(name_norm_to_xrefs.items()):
        if name_norm and len(xrefs) > 1:
            collision_rows.append({
                "NameNormalized": name_norm,
                "Count": len(xrefs),
                "People": "; ".join(f"{xref}:{display_name(ged.indis[xref])}" for xref in sorted(xrefs)),
            })

    root_candidate_rows = []
    for xref, indi in ged.indis.items():
        if len(parents.get(xref, set())) == 0:
            root_candidate_rows.append({
                "Xref": xref,
                "Name": display_name(indi),
                "Sex": indi.sex,
                "DescendantCount": descendants_count(xref, children),
                "SpouseCount": len(spouses.get(xref, set())),
            })
    root_candidate_rows.sort(key=lambda row: (-int(row["DescendantCount"]), str(row["Name"])))

    write_tsv(out_dir / "persons.compact.tsv", list(person_rows[0].keys()) if person_rows else ["Xref"], person_rows)
    write_tsv(out_dir / "families.compact.tsv", list(family_rows[0].keys()) if family_rows else ["Xref"], family_rows)
    write_tsv(out_dir / "life_events_with_places.tsv", list(place_event_rows[0].keys()) if place_event_rows else [
        "PersonXref", "PersonUuid", "PersonName", "EventTag", "EventLabel", "EventType", "Value",
        "DateRaw", "DateFrom", "DateTo", "DatePrecision", "DateModifier", "DateStatus", "DateWarningKind", "YearFrom", "YearTo", "PlaceRaw", "PlaceNormalized", "RelatedXref", "Derived", "Note",
    ], place_event_rows)
    write_tsv(out_dir / "places.compact.tsv", list(place_rows[0].keys()) if place_rows else ["PlaceRaw"], place_rows)
    write_tsv(out_dir / "name_collisions.tsv", ["NameNormalized", "Count", "People"], collision_rows)
    write_tsv(out_dir / "unresolved_refs.tsv", ["type", "source", "role", "target"], ged.unresolved_refs)
    write_tsv(out_dir / "root_candidates.tsv", ["Xref", "Name", "Sex", "DescendantCount", "SpouseCount"], root_candidate_rows)

    summary = {
        "input": str(Path(args.gedcom).resolve()),
        "personCount": len(ged.indis),
        "familyCount": len(ged.fams),
        "eventCountIncludingDerived": len(event_rows),
        "placeLinkedEventCount": len(place_event_rows),
        "distinctPlaceCount": len(place_counter),
        "unresolvedReferenceCount": len(ged.unresolved_refs),
        "nameCollisionCount": len(collision_rows),
        "eventTypeCounts": dict(event_counter.most_common()),
        "topPlaces": [
            {
                "placeRaw": place,
                "eventCount": count,
                "personCount": len(place_people[place]),
            }
            for place, count in place_counter.most_common(20)
        ],
        "outputs": {
            "persons": "persons.compact.tsv",
            "families": "families.compact.tsv",
            "places": "places.compact.tsv",
            "lifeEventsWithPlaces": "life_events_with_places.tsv",
            "rootCandidates": "root_candidates.tsv",
            "nameCollisions": "name_collisions.tsv",
            "unresolvedRefs": "unresolved_refs.tsv",
        },
    }

    (out_dir / "summary.json").write_text(
        json.dumps(summary, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    print(json.dumps(summary, ensure_ascii=False, indent=2))
    return 0


def gather_context(start: str, edges: Dict[str, Set[str]], max_depth: int) -> Dict[str, int]:
    found = {start: 0}
    queue = deque([start])
    while queue:
        current = queue.popleft()
        depth = found[current]
        if depth >= max_depth:
            continue
        for nxt in edges.get(current, set()):
            if nxt not in found:
                found[nxt] = depth + 1
                queue.append(nxt)
    return found


def run_extract_person(args: argparse.Namespace) -> int:
    ged = parse_gedcom(Path(args.gedcom))
    root = resolve_person(ged, args.person)
    parents, children, spouses = build_graphs(ged)

    ancestor_depths = gather_context(root, parents, args.generations_up)
    descendant_depths = gather_context(root, children, args.generations_down)

    people = set(ancestor_depths) | set(descendant_depths) | spouses.get(root, set()) | parents.get(root, set()) | children.get(root, set())
    for xref in list(people):
        people.update(spouses.get(xref, set()) if ancestor_depths.get(xref, 99) <= 1 or descendant_depths.get(xref, 99) <= 1 else set())

    payload = {
        "person": person_payload(ged, root),
        "parents": [person_payload(ged, xref) for xref in sorted(parents.get(root, set()))],
        "spouses": [person_payload(ged, xref) for xref in sorted(spouses.get(root, set()))],
        "children": [person_payload(ged, xref) for xref in sorted(children.get(root, set()))],
        "ancestors": [
            {**person_payload(ged, xref), "depth": depth}
            for xref, depth in sorted(ancestor_depths.items(), key=lambda item: (item[1], display_name(ged.indis.get(item[0]))))
            if xref != root
        ],
        "descendants": [
            {**person_payload(ged, xref), "depth": depth}
            for xref, depth in sorted(descendant_depths.items(), key=lambda item: (item[1], display_name(ged.indis.get(item[0]))))
            if xref != root
        ],
        "lifeEventsWithPlaces": [
            event_to_row(ged, xref, event)
            for xref, event in iter_person_events(ged)
            if xref in people and event.place_raw
        ],
    }

    text = json.dumps(payload, ensure_ascii=False, indent=2) + "\n"
    if args.out:
        Path(args.out).write_text(text, encoding="utf-8")
    else:
        print(text)
    return 0


def person_payload(ged: Gedcom, xref: str) -> Dict[str, object]:
    indi = ged.indis[xref]
    birth = first_event(indi, "BIRT")
    death = first_event(indi, "DEAT")
    return {
        "xref": xref,
        "uuid": person_uuid(xref),
        "name": display_name(indi),
        "sex": indi.sex,
        "birthYear": birth.year_from if birth else None,
        "birthPlace": birth.place_raw if birth else None,
        "deathYear": death.year_from if death else None,
        "deathPlace": death.place_raw if death else None,
    }


def run_validate_scope(args: argparse.Namespace) -> int:
    ged = parse_gedcom(Path(args.gedcom))
    root = resolve_person(ged, args.root)
    out_dir = Path(args.out) if args.out else None
    if out_dir:
        out_dir.mkdir(parents=True, exist_ok=True)

    parents, children, spouses = build_graphs(ged)
    blood = blood_relatives(root, parents, children)

    allowed: Dict[str, str] = {xref: "BLOOD" for xref in blood}
    for blood_xref in blood:
        for spouse in spouses.get(blood_xref, set()):
            allowed.setdefault(spouse, f"AFFINAL_SPOUSE_OF:{blood_xref}")
            for spouse_parent in parents.get(spouse, set()):
                allowed.setdefault(spouse_parent, f"AFFINAL_SPOUSE_PARENT_OF:{spouse}")

    invalid = sorted(set(ged.indis) - set(allowed))

    allowed_rows = [
        {
            "Xref": xref,
            "Name": display_name(ged.indis[xref]),
            "AllowedReason": reason,
        }
        for xref, reason in sorted(allowed.items(), key=lambda item: display_name(ged.indis[item[0]]))
    ]
    invalid_rows = [
        classify_invalid(ged, xref, blood, allowed, parents, children, spouses)
        for xref in invalid
    ]

    summary = {
        "root": person_payload(ged, root),
        "personCount": len(ged.indis),
        "allowedCount": len(allowed),
        "bloodRelativeCount": len(blood),
        "invalidCount": len(invalid),
        "valid": len(invalid) == 0,
        "invalidSamples": invalid_rows[:20],
        "policy": {
            "allowed": [
                "Root person's blood relatives via parent-child graph",
                "Spouses of blood relatives",
                "Parents of spouses of blood relatives",
                "Children of blood relatives through parent-child graph",
            ],
            "disallowedExamples": [
                "Siblings of a spouse when they are not blood relatives",
                "Grandparents of a spouse",
                "Children of a spouse with another partner",
                "Disconnected branches",
            ],
        },
    }

    if out_dir:
        write_tsv(out_dir / "allowed_persons.tsv", ["Xref", "Name", "AllowedReason"], allowed_rows)
        write_tsv(out_dir / "invalid_persons.tsv", ["Xref", "Name", "Reason", "NearestAllowed"], invalid_rows)
        (out_dir / "scope_validation.json").write_text(
            json.dumps(summary, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
        )
        write_invalid_markdown(out_dir / "invalid_branches.md", summary, invalid_rows)

    print(json.dumps(summary, ensure_ascii=False, indent=2))
    return 0 if len(invalid) == 0 or args.allow_invalid_exit_zero else 3


def classify_invalid(
    ged: Gedcom,
    xref: str,
    blood: Set[str],
    allowed: Dict[str, str],
    parents: Dict[str, Set[str]],
    children: Dict[str, Set[str]],
    spouses: Dict[str, Set[str]],
) -> Dict[str, str]:
    nearest: List[str] = []
    if parents.get(xref, set()) & set(allowed):
        nearest.extend(f"child-of:{p}" for p in sorted(parents[xref] & set(allowed)))
    if children.get(xref, set()) & set(allowed):
        nearest.extend(f"parent-of:{c}" for c in sorted(children[xref] & set(allowed)))
    if spouses.get(xref, set()) & set(allowed):
        nearest.extend(f"spouse-of:{s}" for s in sorted(spouses[xref] & set(allowed)))

    if spouses.get(xref, set()) & blood:
        reason = "Unexpected: spouse of blood relative should be allowed"
    elif parents.get(xref, set()) & (set(allowed) - blood):
        reason = "Child of non-blood allowed spouse/affinal person"
    elif children.get(xref, set()) & (set(allowed) - blood):
        reason = "Parent/grandparent beyond allowed affinal degree"
    elif spouses.get(xref, set()) & (set(allowed) - blood):
        reason = "Spouse of affinal person, not spouse of blood relative"
    else:
        reason = "Disconnected or outside allowed blood/affinal scope"

    return {
        "Xref": xref,
        "Name": display_name(ged.indis[xref]),
        "Reason": reason,
        "NearestAllowed": ";".join(nearest[:8]),
    }


def write_invalid_markdown(path: Path, summary: Dict[str, object], invalid_rows: List[Dict[str, str]]) -> None:
    lines = [
        "# GEDCOM Scope Validation",
        "",
        f"Root: {summary['root']['name']} ({summary['root']['xref']})",
        f"Valid: {summary['valid']}",
        f"Invalid persons: {summary['invalidCount']}",
        "",
        "## Invalid Samples",
        "",
    ]
    for row in invalid_rows[:100]:
        lines.append(f"- {row['Xref']} {row['Name']}: {row['Reason']} [{row['NearestAllowed']}]")
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def run_export_staging_tsv(args: argparse.Namespace) -> int:
    gedcom_path = Path(args.gedcom)
    out_dir = Path(args.out)
    out_dir.mkdir(parents=True, exist_ok=True)

    ged = parse_gedcom(gedcom_path)
    batch_id = str(uuid.UUID(args.batch_id)) if args.batch_id else str(uuid.uuid4())
    root_xref = resolve_person(ged, args.root) if args.root else None
    tree_id = uuid.UUID(args.tree_id)
    legacy_ids = bool(args.legacy_ids)
    uuid_scope = TreeUuidScope(tree_id=tree_id, legacy_ids=legacy_ids)

    parents, _children, _spouses = build_graphs(ged)
    unnamed_labels = build_unnamed_person_labels(ged)

    person_rows: List[Dict[str, object]] = []
    name_rows: List[Dict[str, object]] = []
    parsed_name_rows: List[Dict[str, object]] = []
    place_map: Dict[str, Dict[str, object]] = {}
    family_rows: List[Dict[str, object]] = []
    family_child_rows: List[Dict[str, object]] = []
    parent_rows: List[Dict[str, object]] = []
    spouse_rows: List[Dict[str, object]] = []
    event_rows: List[Dict[str, object]] = []
    date_warning_rows: List[Dict[str, object]] = []
    event_citation_rows: List[Dict[str, object]] = []

    name_row_no = 0
    parsed_name_row_no = 0
    for xref, indi in sorted(ged.indis.items()):
        raw_name = unnamed_labels.get(xref) or (indi.names[0] if indi.names else xref)
        variants = build_name_variants(raw_name)
        primary = pick_primary_variant(variants, raw_name)
        tree_person_id = person_uuid(xref, uuid_scope)
        birth = first_event(indi, "BIRT")
        death = first_event(indi, "DEAT")
        is_living = guess_is_living(birth.year_to if birth else None, death.year_from if death else None)

        person_rows.append({
            "ImportBatchId": batch_id,
            "TreePersonId": tree_person_id,
            "ExternalId": xref,
            "Sex": indi.sex if indi.sex in ("M", "F") else None,
            "IsLiving": bit_or_null(is_living),
            "PrimaryDisplayName": primary["FullName"],
            "SurnameNormalized": norm_text(primary["Surname"]) if primary["Surname"] else None,
        })

        if not variants:
            variants = [primary]
        for variant in variants:
            name_row_no += 1
            name_type = "Primary" if norm_text(variant["FullName"]) == norm_text(primary["FullName"]) else "Variant"
            name_rows.append({
                "ImportBatchId": batch_id,
                "RowNumber": name_row_no,
                "TreePersonId": tree_person_id,
                "ScriptCode": variant["ScriptCode"],
                "NameType": name_type,
                "Given": variant["Given"],
                "Surname": variant["Surname"],
                "FullName": variant["FullName"],
                "FullNameNormalized": variant["FullNameNormalized"],
                "IsPrimary": 1 if norm_text(variant["FullName"]) == norm_text(primary["FullName"]) else 0,
            })
            parsed_name_row_no += 1
            parsed_name_rows.append(normalized_name_to_row(
                batch_id=batch_id,
                row_number=parsed_name_row_no,
                source_name_row_number=name_row_no,
                tree_person_id=tree_person_id,
                name_type=name_type,
                normalized=normalize_name_for_preflight(raw_name, variant),
            ))

    for fam_xref, fam in sorted(ged.fams.items()):
        marriage = first_family_event(fam, "MARR")
        marriage_place = norm_space(marriage.place_raw) if marriage and marriage.place_raw else None
        if marriage_place:
            place_map.setdefault(marriage_place, {
                "PlaceRaw": marriage_place,
                "PlaceNormalized": normalize_place_key(marriage_place),
            })

        family_rows.append({
            "ImportBatchId": batch_id,
            "FamilyId": family_uuid(fam_xref, uuid_scope),
            "ExternalId": fam_xref,
            "Spouse1TreePersonId": person_uuid(fam.husb, uuid_scope) if fam.husb in ged.indis else None,
            "Spouse2TreePersonId": person_uuid(fam.wife, uuid_scope) if fam.wife in ged.indis else None,
            "MarriageDateRaw": marriage.date_raw if marriage else None,
            "MarriageYear": marriage.year_from if marriage and marriage.year_from is not None else (marriage.year_to if marriage else None),
            "MarriagePlaceRaw": marriage_place,
            "Notes": marriage.note if marriage else None,
        })

        for child in fam.chil:
            if child in ged.indis:
                family_child_rows.append({
                    "ImportBatchId": batch_id,
                    "FamilyId": family_uuid(fam_xref, uuid_scope),
                    "ChildTreePersonId": person_uuid(child, uuid_scope),
                })

        family_parents = [xref for xref in (fam.husb, fam.wife) if xref in ged.indis]
        for child in fam.chil:
            if child not in ged.indis:
                continue
            for parent in family_parents:
                parent_rows.append({
                    "ImportBatchId": batch_id,
                    "ParentTreePersonId": person_uuid(parent, uuid_scope),
                    "ChildTreePersonId": person_uuid(child, uuid_scope),
                    "RelationType": "BIO",
                })

        if fam.husb in ged.indis and fam.wife in ged.indis:
            for from_xref, to_xref in ((fam.husb, fam.wife), (fam.wife, fam.husb)):
                spouse_rows.append({
                    "ImportBatchId": batch_id,
                    "FromTreePersonId": person_uuid(from_xref, uuid_scope),
                    "ToTreePersonId": person_uuid(to_xref, uuid_scope),
                    "RelationType": "MARR",
                    "FamilyId": family_uuid(fam_xref, uuid_scope),
                    "MarriageYear": marriage.year_from if marriage and marriage.year_from is not None else (marriage.year_to if marriage else None),
                    "MarriagePlaceRaw": marriage_place,
                })

    event_ordinal_by_person: DefaultDict[str, int] = defaultdict(int)
    event_row_no = 0
    for xref, event in iter_person_events(ged):
        if xref not in ged.indis:
            continue
        if event.tag == "BURI":
            continue
        event_ordinal_by_person[xref] += 1
        event_row_no += 1
        place_raw = norm_space(event.place_raw) if event.place_raw else None
        if place_raw:
            place_map.setdefault(place_raw, {
                "PlaceRaw": place_raw,
                "PlaceNormalized": normalize_place_key(place_raw),
            })
        related_tree_person_id = person_uuid(event.related_xref, uuid_scope) if event.related_xref in ged.indis else None
        family_id = family_uuid(event.related_xref, uuid_scope) if event.related_xref in ged.fams else None
        event_rows.append({
            "ImportBatchId": batch_id,
            "RowNumber": event_row_no,
            "ExternalEventKey": event_identity_key(xref, event, event_ordinal_by_person[xref]),
            "TreePersonId": person_uuid(xref, uuid_scope),
            "EventType": event.tag,
            "EventValue": event.value,
            "DateRaw": event.date_raw,
            "DateFrom": event.date_from,
            "DateTo": event.date_to,
            "YearFrom": event.year_from,
            "YearTo": event.year_to,
            "PlaceRaw": place_raw,
            "PlaceNormalized": normalize_place_key(place_raw),
            "FamilyId": family_id,
            "RelatedTreePersonId": related_tree_person_id,
            "IsDerived": 1 if event.derived else 0,
            "Notes": event.note,
        })
        warning_kind = date_warning_kind(event)
        if warning_kind:
            date_warning_rows.append({
                "ImportBatchId": batch_id,
                "EventRowNumber": event_row_no,
                "TreePersonId": person_uuid(xref, uuid_scope),
                "PersonXref": xref,
                "PersonName": display_name(ged.indis.get(xref)),
                "EventType": event.tag,
                "DateRaw": event.date_raw,
                "DateFrom": event.date_from,
                "DateTo": event.date_to,
                "DatePrecision": event.date_precision,
                "DateModifier": event.date_modifier,
                "DateStatus": event.date_status,
                "WarningKind": warning_kind,
                "WarningMessage": date_warning_message(event),
            })
        should_export_citations = event.tag in {"BIRT", "CHR", "DEAT"} and not event.derived
        for citation in (event.citations if should_export_citations else []):
            event_citation_rows.append({
                "ImportBatchId": batch_id,
                "RowNumber": len(event_citation_rows) + 1,
                "EventRowNumber": event_row_no,
                "SourceRef": citation.source_ref,
                "SourceTitle": source_title_for_citation(ged, citation),
                "Page": citation.page,
                "Quality": citation.quality,
                "CitationDateRaw": citation.citation_date_raw,
                "CitationText": citation.text,
                "Note": citation.note,
            })

    place_rows = []
    for row_no, place in enumerate(sorted(place_map), start=1):
        place_rows.append({
            "ImportBatchId": batch_id,
            "RowNumber": row_no,
            **place_map[place],
        })

    parent_rows = dedupe_rows(parent_rows, ("ImportBatchId", "ParentTreePersonId", "ChildTreePersonId", "RelationType"))
    family_child_rows = dedupe_rows(family_child_rows, ("ImportBatchId", "FamilyId", "ChildTreePersonId"))

    outputs = {
        "gedcom_import_person.tsv": (
            ["ImportBatchId", "TreePersonId", "ExternalId", "Sex", "IsLiving", "PrimaryDisplayName", "SurnameNormalized"],
            person_rows,
        ),
        "gedcom_import_person_name.tsv": (
            ["ImportBatchId", "RowNumber", "TreePersonId", "ScriptCode", "NameType", "Given", "Surname", "FullName", "FullNameNormalized", "IsPrimary"],
            name_rows,
        ),
        "gedcom_import_person_name_parsed.tsv": (
            [
                "ImportBatchId",
                "RowNumber",
                "SourceNameRowNumber",
                "TreePersonId",
                "RawName",
                "NameType",
                "ScriptCode",
                "GivenName",
                "Patronymic",
                "Surname",
                "MaidenSurname",
                "MarriedSurname",
                "TitlePrefix",
                "Suffix",
                "LanguageHint",
                "GivenNameNormalized",
                "PatronymicNormalized",
                "SurnameNormalized",
                "FullNameNormalized",
                "NameTokens",
                "VariantExplanation",
                "NormalizationConfidence",
                "ParserStatus",
            ],
            parsed_name_rows,
        ),
        "gedcom_import_place.tsv": (
            ["ImportBatchId", "RowNumber", "PlaceRaw", "PlaceNormalized"],
            place_rows,
        ),
        "gedcom_import_family.tsv": (
            ["ImportBatchId", "FamilyId", "ExternalId", "Spouse1TreePersonId", "Spouse2TreePersonId", "MarriageDateRaw", "MarriageYear", "MarriagePlaceRaw", "Notes"],
            family_rows,
        ),
        "gedcom_import_family_child.tsv": (
            ["ImportBatchId", "FamilyId", "ChildTreePersonId"],
            family_child_rows,
        ),
        "gedcom_import_parent_of.tsv": (
            ["ImportBatchId", "ParentTreePersonId", "ChildTreePersonId", "RelationType"],
            parent_rows,
        ),
        "gedcom_import_spouse_of.tsv": (
            ["ImportBatchId", "RowNumber", "FromTreePersonId", "ToTreePersonId", "RelationType", "FamilyId", "MarriageYear", "MarriagePlaceRaw"],
            [{**row, "RowNumber": idx} for idx, row in enumerate(spouse_rows, start=1)],
        ),
        "gedcom_import_event.tsv": (
            ["ImportBatchId", "RowNumber", "ExternalEventKey", "TreePersonId", "EventType", "EventValue", "DateRaw", "DateFrom", "DateTo", "YearFrom", "YearTo", "PlaceRaw", "PlaceNormalized", "FamilyId", "RelatedTreePersonId", "IsDerived", "Notes"],
            event_rows,
        ),
        "gedcom_import_event_citation.tsv": (
            ["ImportBatchId", "RowNumber", "EventRowNumber", "SourceRef", "SourceTitle", "Page", "Quality", "CitationDateRaw", "CitationText", "Note"],
            event_citation_rows,
        ),
        "date_parse_warnings.tsv": (
            ["ImportBatchId", "EventRowNumber", "TreePersonId", "PersonXref", "PersonName", "EventType", "DateRaw", "DateFrom", "DateTo", "DatePrecision", "DateModifier", "DateStatus", "WarningKind", "WarningMessage"],
            date_warning_rows,
        ),
    }

    counts = {}
    for filename, (headers, rows) in outputs.items():
        counts[filename] = write_tsv(out_dir / filename, headers, rows, encoding="utf-16")

    source_hash = sha256_file(gedcom_path)
    root_tree_person_id = person_uuid(root_xref, uuid_scope) if root_xref else None

    manifest = {
        "batchId": batch_id,
        "sourceFilePath": str(gedcom_path.resolve()),
        "sourceFileHash": source_hash,
        "treeId": str(tree_id),
        "treeName": args.tree_name,
        "uuidMode": "legacy" if legacy_ids else "tree-scoped",
        "rootExternalId": root_xref,
        "rootTreePersonId": root_tree_person_id,
        "counts": counts,
        "encoding": "utf-16",
        "artifacts": {
            "dateParseWarnings": "date_parse_warnings.tsv",
        },
        "loadOrder": [
            "gedcom_import_person.tsv",
            "gedcom_import_person_name.tsv",
            "gedcom_import_person_name_parsed.tsv",
            "gedcom_import_place.tsv",
            "gedcom_import_family.tsv",
            "gedcom_import_family_child.tsv",
            "gedcom_import_parent_of.tsv",
            "gedcom_import_spouse_of.tsv",
            "gedcom_import_event.tsv",
            "date_parse_warnings.tsv",
            "gedcom_import_event_citation.tsv",
        ],
    }
    (out_dir / "staging_manifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    print(json.dumps(manifest, ensure_ascii=False, indent=2))
    return 0


def first_family_event(fam: Fam, tag: str) -> Optional[Event]:
    for event in fam.events:
        if event.tag == tag:
            return event
    return None


def dedupe_rows(rows: List[Dict[str, object]], keys: Tuple[str, ...]) -> List[Dict[str, object]]:
    seen: Set[Tuple[object, ...]] = set()
    out: List[Dict[str, object]] = []
    for row in rows:
        key = tuple(row.get(k) for k in keys)
        if key in seen:
            continue
        seen.add(key)
        out.append(row)
    return out


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="GEDCOM preflight and compact extraction tool")
    sub = parser.add_subparsers(dest="command", required=True)

    inspect = sub.add_parser("inspect", help="Create compact analysis artifacts from a GEDCOM file")
    inspect.add_argument("gedcom")
    inspect.add_argument("out")
    inspect.set_defaults(func=run_inspect)

    extract = sub.add_parser("extract-person", help="Extract compact context around one person")
    extract.add_argument("gedcom")
    extract.add_argument("person", help="GEDCOM xref such as @I1@, or an unambiguous name substring")
    extract.add_argument("--generations-up", type=int, default=3)
    extract.add_argument("--generations-down", type=int, default=2)
    extract.add_argument("--out")
    extract.set_defaults(func=run_extract_person)

    validate = sub.add_parser("validate-scope", help="Validate root tree contains only blood relatives and allowed affinal relatives")
    validate.add_argument("gedcom")
    validate.add_argument("--root", required=True, help="Root person xref or unambiguous name substring")
    validate.add_argument("--out")
    validate.add_argument("--allow-invalid-exit-zero", action="store_true")
    validate.set_defaults(func=run_validate_scope)

    staging = sub.add_parser("export-staging-tsv", help="Export GEDCOM rows as database-neutral staging TSV/JSON artifacts")
    staging.add_argument("gedcom")
    staging.add_argument("out")
    staging.add_argument("--batch-id", help="Optional fixed import batch GUID")
    staging.add_argument("--root", help="Optional root person xref or unambiguous name substring")
    staging.add_argument("--tree-id", required=True, help="Target tree dataset TreeId (UUID). Required.")
    staging.add_argument("--tree-name", help="Target tree name for manifest readability")
    staging.add_argument("--legacy-ids", action="store_true", help="Use legacy UUIDs derived only from the GEDCOM xref instead of tree-scoped keys.")
    staging.add_argument("--notes")
    staging.set_defaults(func=run_export_staging_tsv)

    return parser


def main(argv: Optional[List[str]] = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    raise SystemExit(main())
