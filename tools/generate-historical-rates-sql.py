#!/usr/bin/env python3
"""
Generate SQL INSERT statements for CCURRA exchange rates from RBA data sources.

Supports two modes:
  --xls    Parse RBA 2018-2022.xls (old .xls BIFF format) for historical rates
  --csv    Parse RBA f11.1-data.csv (Jan 2023 → present) for recent rates

Both modes generate idempotent INSERT statements with NOT EXISTS guards
for loading daily SPOT rates into mvxcdta.CCURRA.

Usage:
    # 2020-2022 from XLS:
    python generate-historical-rates-sql.py --xls 2018-2022.xls --from-year 2020 --to-year 2022

    # 2023-present from CSV (downloads live from RBA):
    python generate-historical-rates-sql.py --csv --from-year 2023

    # 2023-present from local CSV file:
    python generate-historical-rates-sql.py --csv f11.1-data.csv --from-year 2023

    # Custom output file:
    python generate-historical-rates-sql.py --csv --from-year 2023 --output rates-2023.sql

Requires: xlrd (pip install xlrd) — only needed for --xls mode
"""

import argparse
import sys
from datetime import date, datetime
from pathlib import Path

# Currencies to extract (all confirmed present in both RBA XLS and CSV)
# Default list; can be overridden via --currencies CLI argument
DEFAULT_CURRENCIES = ["USD", "HKD", "EUR", "JPY", "GBP", "NZD", "MYR", "SGD"]
TARGET_CURRENCIES = DEFAULT_CURRENCIES

# Fixed CCURRA field values per expert-movex-dotnet field mapping
CUCONO = 100
CUDIVI = "D"
CUGLOC = " "
CUCRTP = "99"
CUTXID = 0
CULOCD = "AUD"
CUDMCU = 2
CURAFA = 4
CURGTM = 100000  # fixed time stamp for batch loads
CUCHNO = 1
CUCHID = "SRXAPI"

RBA_XLS_URL = "https://www.rba.gov.au/statistics/tables/xls-hist/2018-2022.xls"
RBA_CSV_URL = "https://www.rba.gov.au/statistics/tables/csv/f11.1-data.csv"


# ── XLS parsing (2018-2022) ──────────────────────────────────────────────────

def parse_xls(filepath: Path, from_date: date, to_date: date) -> dict[str, list[tuple[date, float]]]:
    """
    Parse the RBA historical XLS (.xls BIFF format) and return rates per currency.
    Requires: xlrd
    """
    try:
        import xlrd
    except ImportError:
        print("ERROR: xlrd is required for --xls mode. Install with: pip install xlrd", file=sys.stderr)
        sys.exit(1)

    wb = xlrd.open_workbook(filepath)
    ws = wb.sheet_by_index(0)

    # Find the Units header row and map currency → column index
    units_row_idx = None
    currency_col_map: dict[str, int] = {}

    for row_idx in range(ws.nrows):
        first_cell = ws.cell_value(row_idx, 0)
        if first_cell and str(first_cell).strip().lower() == "units":
            units_row_idx = row_idx
            for col_idx in range(ws.ncols):
                val = str(ws.cell_value(row_idx, col_idx)).strip().upper()
                if val in TARGET_CURRENCIES:
                    currency_col_map[val] = col_idx
            break

    if units_row_idx is None:
        print("ERROR: Could not find 'Units' header row in the XLS file.", file=sys.stderr)
        sys.exit(1)

    _warn_missing(currency_col_map, "XLS")

    results: dict[str, list[tuple[date, float]]] = {c: [] for c in currency_col_map}

    for row_idx in range(units_row_idx + 1, ws.nrows):
        date_cell = ws.cell(row_idx, 0)

        if date_cell.ctype in (xlrd.XL_CELL_DATE, xlrd.XL_CELL_NUMBER):
            try:
                dt_tuple = xlrd.xldate_as_tuple(date_cell.value, wb.datemode)
                row_date = date(dt_tuple[0], dt_tuple[1], dt_tuple[2])
            except (ValueError, xlrd.XLDateError):
                continue
        elif date_cell.ctype == xlrd.XL_CELL_TEXT:
            row_date = _parse_date_str(str(date_cell.value).strip())
            if row_date is None:
                continue
        else:
            continue

        if row_date < from_date or row_date > to_date:
            continue

        for currency, col_idx in currency_col_map.items():
            cell = ws.cell(row_idx, col_idx)
            if cell.ctype not in (xlrd.XL_CELL_NUMBER, xlrd.XL_CELL_TEXT):
                continue
            _try_add_rate(results, currency, row_date, cell.value)

    return results


# ── CSV parsing (2023-present) ────────────────────────────────────────────────

def parse_csv(source: str, from_date: date, to_date: date) -> dict[str, list[tuple[date, float]]]:
    """
    Parse the RBA f11.1-data.csv and return rates per currency.
    source: file path or CSV string content.
    """
    if Path(source).exists():
        csv_text = Path(source).read_text(encoding="utf-8")
    else:
        csv_text = source

    lines = csv_text.split("\n")

    # Find the "Units" header row
    units_line_idx = None
    for i, line in enumerate(lines):
        if line.strip().lower().startswith("units,"):
            units_line_idx = i
            break

    if units_line_idx is None:
        print("ERROR: Could not find 'Units' header row in the CSV.", file=sys.stderr)
        sys.exit(1)

    headers = lines[units_line_idx].split(",")
    currency_col_map: dict[str, int] = {}
    for col_idx, h in enumerate(headers):
        val = h.strip().upper()
        if val in TARGET_CURRENCIES:
            currency_col_map[val] = col_idx

    _warn_missing(currency_col_map, "CSV")

    results: dict[str, list[tuple[date, float]]] = {c: [] for c in currency_col_map}

    for i in range(units_line_idx + 1, len(lines)):
        line = lines[i].strip()
        if not line or not line[0].isdigit():
            continue

        cols = line.split(",")
        row_date = _parse_date_str(cols[0].strip())
        if row_date is None:
            continue

        if row_date < from_date or row_date > to_date:
            continue

        for currency, col_idx in currency_col_map.items():
            if col_idx >= len(cols):
                continue
            _try_add_rate(results, currency, row_date, cols[col_idx].strip())

    return results


def download_csv() -> str:
    """Download the RBA f11.1-data.csv and return contents as string."""
    import urllib.request
    import ssl

    ctx = ssl.create_default_context()
    print(f"Downloading {RBA_CSV_URL} ...")
    req = urllib.request.Request(RBA_CSV_URL, headers={"User-Agent": "SM-Reporting-Service/1.0"})
    with urllib.request.urlopen(req, context=ctx) as resp:
        data = resp.read().decode("utf-8")
    print(f"Downloaded {len(data):,} bytes")
    return data


def download_xls(output_path: Path) -> Path:
    """Download the RBA 2018-2022 XLS file."""
    import urllib.request
    import ssl

    ctx = ssl.create_default_context()
    print(f"Downloading {RBA_XLS_URL} ...")
    urllib.request.urlretrieve(RBA_XLS_URL, output_path, context=ctx)
    print(f"Saved to {output_path}")
    return output_path


# ── Shared helpers ────────────────────────────────────────────────────────────

def _parse_date_str(s: str) -> date | None:
    """Try multiple date formats common in RBA data."""
    if not s:
        return None
    for fmt in ("%d-%b-%Y", "%Y-%m-%d", "%d/%m/%Y"):
        try:
            return datetime.strptime(s, fmt).date()
        except ValueError:
            continue
    return None


def _try_add_rate(results: dict, currency: str, row_date: date, raw_value) -> None:
    """Validate and append a rate value."""
    try:
        rate = float(raw_value)
    except (ValueError, TypeError):
        return

    if rate < 0.0001 or rate > 10000:
        print(f"WARNING: Rate {rate} for {currency} on {row_date} outside valid range, skipping",
              file=sys.stderr)
        return

    results[currency].append((row_date, rate))


def _warn_missing(currency_col_map: dict[str, int], source_name: str) -> None:
    """Warn about any target currencies not found in the source."""
    missing = set(TARGET_CURRENCIES) - set(currency_col_map.keys())
    if missing:
        print(f"WARNING: Currencies not found in {source_name}: {', '.join(sorted(missing))}", file=sys.stderr)


def to_m3_date(d: date) -> int:
    """Convert date to M3 numeric YYYYMMDD format."""
    return d.year * 10000 + d.month * 100 + d.day


def generate_sql(rates: dict[str, list[tuple[date, float]]], today: date, source_label: str) -> list[str]:
    """
    Generate idempotent INSERT statements with NOT EXISTS guard.

    Each statement uses SELECT ... FROM SYSIBM.SYSDUMMY1 WHERE NOT EXISTS
    to avoid duplicate PK violations on (CUCONO, CUDIVI, CUCUCD, CUCRTP, CUCUTD).
    """
    today_int = to_m3_date(today)
    statements: list[str] = []

    statements.append(f"-- CCURRA Exchange Rate Backfill")
    statements.append(f"-- Generated: {today.isoformat()}")
    statements.append(f"-- Source: {source_label}")
    statements.append(f"-- Currencies: {', '.join(sorted(rates.keys()))}")
    statements.append(f"-- CUCHID: {CUCHID}")
    statements.append("")

    for currency in sorted(rates.keys()):
        currency_rates = sorted(rates[currency], key=lambda x: x[0])
        if not currency_rates:
            continue

        statements.append(f"-- {currency}: {len(currency_rates)} rates "
                          f"({currency_rates[0][0].isoformat()} to {currency_rates[-1][0].isoformat()})")

        for row_date, rate in currency_rates:
            date_int = to_m3_date(row_date)
            statements.append(
                f"INSERT INTO mvxcdta.CCURRA "
                f"(CUCONO,CUDIVI,CUGLOC,CUCUCD,CUCRTP,CUCUTD,CUARAT,"
                f"CUTXID,CULOCD,CUDMCU,CURAFA,CURGDT,CURGTM,CULMDT,CUCHNO,CUCHID) "
                f"SELECT {CUCONO},'{CUDIVI}','{CUGLOC}','{currency}','{CUCRTP}',{date_int},{rate},"
                f"{CUTXID},'{CULOCD}',{CUDMCU},{CURAFA},{date_int},{CURGTM},{today_int},{CUCHNO},'{CUCHID}' "
                f"FROM SYSIBM.SYSDUMMY1 "
                f"WHERE NOT EXISTS ("
                f"SELECT 1 FROM mvxcdta.CCURRA "
                f"WHERE CUCONO={CUCONO} AND CUDIVI='{CUDIVI}' "
                f"AND CUCUCD='{currency}' AND CUCRTP='{CUCRTP}' AND CUCUTD={date_int});"
            )

        statements.append("")

    return statements


def print_summary(rates: dict[str, list[tuple[date, float]]]) -> int:
    """Print dry-run summary and return total count."""
    total = 0
    print("=== DRY-RUN SUMMARY ===")
    for currency in sorted(rates.keys()):
        count = len(rates[currency])
        total += count
        if count > 0:
            first = rates[currency][0][0].isoformat()
            last = rates[currency][-1][0].isoformat()
            print(f"  {currency}: {count:>4} rates ({first} to {last})")
        else:
            print(f"  {currency}: no rates found")
    print(f"  TOTAL: {total} INSERT statements")
    print()
    return total


# ── Main ──────────────────────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(
        description="Generate CCURRA INSERT statements from RBA exchange rate data")

    source_group = parser.add_mutually_exclusive_group(required=True)
    source_group.add_argument("--xls", metavar="FILE",
                              help="Parse RBA .xls file (2018-2022 historical)")
    source_group.add_argument("--csv", nargs="?", const="__download__", metavar="FILE",
                              help="Parse RBA f11.1-data.csv (2023-present). "
                                   "Omit FILE to download live from RBA.")
    source_group.add_argument("--download-xls", action="store_true",
                              help="Download the RBA 2018-2022.xls first, then parse")

    parser.add_argument("--from-year", type=int, default=2020,
                        help="Start year (default: 2020)")
    parser.add_argument("--to-year", type=int, default=None,
                        help="End year (default: current year)")
    parser.add_argument("--currencies", type=str, default=None,
                        help="Comma-separated list of currencies to extract (e.g. 'MYR,SGD'). "
                             "Omit to use all default currencies.")
    parser.add_argument("--output", "-o", type=Path,
                        help="Output SQL file (auto-generated if omitted)")
    args = parser.parse_args()

    # Override TARGET_CURRENCIES if --currencies argument provided
    global TARGET_CURRENCIES
    if args.currencies:
        TARGET_CURRENCIES = [c.strip().upper() for c in args.currencies.split(",")]

    to_year = args.to_year or date.today().year
    from_date = date(args.from_year, 1, 1)
    to_date = date(to_year, 12, 31)

    # Clamp to_date to today (don't generate future dates)
    if to_date > date.today():
        to_date = date.today()

    print(f"Date range: {from_date.isoformat()} to {to_date.isoformat()}")
    print(f"Target currencies: {', '.join(TARGET_CURRENCIES)}")
    print()

    if args.xls:
        xls_path = Path(args.xls)
        if not xls_path.exists():
            print(f"ERROR: File not found: {xls_path}", file=sys.stderr)
            sys.exit(1)
        print(f"Parsing XLS: {xls_path}")
        rates = parse_xls(xls_path, from_date, to_date)
        source_label = f"RBA Historical XLS ({args.from_year}-{to_year})"
        default_output = f"ccurra-backfill-{args.from_year}-{to_year}-xls.sql"

    elif args.download_xls:
        xls_path = Path("rba-2018-2022.xls")
        download_xls(xls_path)
        print(f"Parsing XLS: {xls_path}")
        rates = parse_xls(xls_path, from_date, to_date)
        source_label = f"RBA Historical XLS ({args.from_year}-{to_year})"
        default_output = f"ccurra-backfill-{args.from_year}-{to_year}-xls.sql"

    elif args.csv is not None:
        if args.csv == "__download__":
            csv_data = download_csv()
            print(f"Parsing downloaded CSV")
            rates = parse_csv(csv_data, from_date, to_date)
        else:
            csv_path = Path(args.csv)
            if not csv_path.exists():
                print(f"ERROR: File not found: {csv_path}", file=sys.stderr)
                sys.exit(1)
            print(f"Parsing CSV: {csv_path}")
            rates = parse_csv(str(csv_path), from_date, to_date)
        source_label = f"RBA f11.1-data.csv ({args.from_year}-{to_year})"
        default_output = f"ccurra-backfill-{args.from_year}-{to_year}-csv.sql"

    print()
    total = print_summary(rates)

    if total == 0:
        print("No rates found — nothing to generate.")
        sys.exit(0)

    today = date.today()
    output_path = args.output or Path(default_output)
    statements = generate_sql(rates, today, source_label)

    output_path.write_text("\n".join(statements), encoding="utf-8")
    print(f"SQL written to: {output_path}")
    print(f"Review the file, then execute against DB2 via ACS or ODBC.")


if __name__ == "__main__":
    main()
