"""Turns failed tests in TRX files into GitHub annotations, so failures are readable without the raw log."""
import glob
import sys
import xml.etree.ElementTree as ET

# The TRX schema namespace used by every vstest TRX file
NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
# GitHub keeps at most 10 error annotations per step; the last slot summarizes the rest
MAX_DETAILED = 9


def escape(text: str) -> str:
    # Workflow-command escaping: %, CR and LF must be encoded or the annotation is truncated
    return text.replace("%", "%25").replace("\r", "").replace("\n", "%0A")


def main(results_dir: str, expected_projects: list[str]) -> None:
    failures = []
    found = set()
    for path in sorted(glob.glob(f"{results_dir}/**/*.trx", recursive=True)):
        root = ET.parse(path).getroot()
        name = path.rsplit("/", 1)[-1].removesuffix(".trx")
        found.add(name)
        counters = root.find(".//t:Counters", NS)
        if counters is not None:
            # One notice per project with its totals
            print(f"::notice title={name}::total={counters.get('total')} passed={counters.get('passed')} "
                  f"failed={counters.get('failed')} notExecuted={counters.get('notExecuted')}")
        for result in root.iterfind(".//t:UnitTestResult", NS):
            if result.get("outcome") == "Failed":
                message = result.find(".//t:Message", NS)
                stack = result.find(".//t:StackTrace", NS)
                text = (message.text or "") if message is not None else ""
                trace = (stack.text or "")[:700] if stack is not None else ""
                failures.append((result.get("testName", "?"), f"{text}\n{trace}"))
    for project in expected_projects:
        if project not in found:
            # A missing file usually means the test host crashed (for example on a failed Debug.Assert)
            print(f"::error title=No results for {project}::The test host produced no TRX file; it may have crashed.")
    for test_name, text in failures[:MAX_DETAILED]:
        print(f"::error title={escape(test_name)}::{escape(text)}")
    if len(failures) > MAX_DETAILED:
        rest = ", ".join(name for name, _ in failures[MAX_DETAILED:])
        print(f"::error title={len(failures) - MAX_DETAILED} more failures::{escape(rest)}")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2:])
