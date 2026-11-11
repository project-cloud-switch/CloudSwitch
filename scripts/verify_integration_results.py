"""Fail CI when integration discovery is empty, incomplete, skipped, or failed."""

import sys
import xml.etree.ElementTree as ET
from pathlib import Path

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
REQUIRED_SUITES = {
    "CloudSwitch.Api.Tests.PersistenceTests",
    "CloudSwitch.Api.Tests.WorkerPersistenceTests",
    "CloudSwitch.Api.Tests.WorkerMessagingTests",
}


def verify(path: Path) -> int:
    root = ET.parse(path).getroot()
    results = root.findall("t:Results/t:UnitTestResult", NS)
    if not results:
        raise ValueError("No integration tests were discovered.")
    incomplete = [r.get("testName") for r in results if r.get("outcome") != "Passed"]
    if incomplete:
        raise ValueError(f"Integration tests failed or were skipped: {incomplete}")
    counters = root.find("t:ResultSummary/t:Counters", NS)
    if counters is None or any(
        int(counters.get(key, "-1")) != len(results)
        for key in ("total", "executed", "passed")
    ):
        raise ValueError("Integration result counters are missing or incomplete.")
    passed_ids = {r.get("testId") for r in results}
    suites = {
        method.get("className")
        for test in root.findall("t:TestDefinitions/t:UnitTest", NS)
        if test.get("id") in passed_ids
        for method in test.findall("t:TestMethod", NS)
    }
    missing = REQUIRED_SUITES - suites
    if missing:
        raise ValueError(f"Required integration suites did not run: {sorted(missing)}")
    return len(results)


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit("Usage: verify_integration_results.py <integration.trx>")
    try:
        count = verify(Path(sys.argv[1]))
    except (OSError, ET.ParseError, ValueError) as error:
        sys.exit(f"Integration verification failed: {error}")
    print(f"Verified {count} passing integration tests; no skips; all required suites ran.")
