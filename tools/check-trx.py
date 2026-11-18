"""Require an executed, passing TRX result for each fully qualified test name."""
import argparse
import xml.etree.ElementTree as ET

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("trx")
parser.add_argument("cases", nargs="+")
args = parser.parse_args()
root = ET.parse(args.trx).getroot()
counters = root.find(".//{*}Counters")
results = root.findall(".//{*}UnitTestResult")
passed = {result.get("testName") for result in results if result.get("outcome") == "Passed"}
missing = set(args.cases) - passed
if (counters is None or int(counters.get("executed", 0)) == 0
        or int(counters.get("failed", 0)) or missing
        or root.find(".//{*}ResultSummary").get("outcome") != "Completed"):
    raise SystemExit(f"Invalid run: counters={None if counters is None else counters.attrib}, missing={sorted(missing)}")
print(f"{counters.get('passed')} passed, {counters.get('failed')} failed. Verified {len(args.cases)} named case(s).")
