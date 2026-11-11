import importlib.util
import tempfile
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

spec = importlib.util.spec_from_file_location(
    "guard", Path(__file__).parents[1] / "verify_integration_results.py"
)
guard = importlib.util.module_from_spec(spec)
spec.loader.exec_module(guard)


class IntegrationResultsTests(unittest.TestCase):
    def report(self):
        root = ET.Element("TestRun", xmlns=guard.NS["t"])
        results = ET.SubElement(root, "Results")
        definitions = ET.SubElement(root, "TestDefinitions")
        for suite in sorted(guard.REQUIRED_SUITES):
            ET.SubElement(results, "UnitTestResult", testId=suite, testName=suite, outcome="Passed")
            test = ET.SubElement(definitions, "UnitTest", id=suite)
            ET.SubElement(test, "TestMethod", className=suite)
        summary = ET.SubElement(root, "ResultSummary")
        count = str(len(guard.REQUIRED_SUITES))
        ET.SubElement(summary, "Counters", total=count, executed=count, passed=count)
        return root

    def verify(self, root):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "integration.trx"
            ET.ElementTree(root).write(path)
            return guard.verify(path)

    def test_accepts_complete_passing_run(self):
        self.assertEqual(len(guard.REQUIRED_SUITES), self.verify(self.report()))

    def test_rejects_skips_failures_and_unknown_outcomes(self):
        for outcome in ("NotExecuted", "Failed", "Aborted", ""):
            with self.subTest(outcome=outcome):
                root = self.report()
                root.find("Results")[0].set("outcome", outcome)
                with self.assertRaisesRegex(ValueError, "failed or were skipped"):
                    self.verify(root)

    def test_rejects_empty_discovery(self):
        root = self.report()
        root.find("Results").clear()
        with self.assertRaisesRegex(ValueError, "No integration tests"):
            self.verify(root)

    def test_rejects_missing_suite_even_with_passing_results(self):
        root = self.report()
        root.find("TestDefinitions")[0].find("TestMethod").set("className", "OtherSuite")
        with self.assertRaisesRegex(ValueError, "Required integration suites"):
            self.verify(root)

    def test_rejects_incomplete_counters(self):
        root = self.report()
        root.find("ResultSummary/Counters").set("total", "4")
        with self.assertRaisesRegex(ValueError, "counters"):
            self.verify(root)

    def test_rejects_missing_report(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaises(FileNotFoundError):
                guard.verify(Path(directory) / "missing.trx")


if __name__ == "__main__":
    unittest.main()
