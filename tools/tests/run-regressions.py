"""Run Parity's non-interactive regression suites on Windows (.NET, Node, Playwright and Edge)."""

from pathlib import Path
import shutil
import subprocess
import sys

from playwright.sync_api import sync_playwright


ROOT = Path(__file__).resolve().parents[2]


def run(*args):
    subprocess.run(args, cwd=ROOT, check=True)


def main():
    sdk = ROOT / ".build" / "dotnet" / "dotnet.exe"
    dotnet = str(sdk) if sdk.is_file() else shutil.which("dotnet")
    if not dotnet:
        raise SystemExit("Install the .NET SDK specified in global.json.")
    run("node", "ui/node_modules/typescript/bin/tsc", "-p", "ui/tsconfig.json")
    for project in sorted((ROOT / "tools/tests").glob("*/*.csproj")):
        print(project.stem, flush=True)
        run(dotnet, "run", "--project", str(project), "-c", "Release")
    run(sys.executable, "-m", "unittest", "discover", "-s", "tools/tests", "-p", "test_*.py")
    run("node", "ui/tools/test-battery-runtime.mjs")
    run("node", "ui/tools/test-gesture-pad.mjs")
    # These tools generate HTML harnesses. The assertions actually run in Edge.
    with sync_playwright() as playwright:
        browser = playwright.chromium.launch(channel="msedge", args=["--mute-audio"])
        path = subprocess.check_output(["node", "ui/tools/test-localization.mjs"], cwd=ROOT, text=True).strip()
        page = browser.new_page()
        page.goto(Path(path).as_uri())
        perf = page.context.new_cdp_session(page)
        perf.send("Performance.enable")

        def collect_nodes():
            perf.send("HeapProfiler.collectGarbage")
            return next(metric["value"] for metric in perf.send("Performance.getMetrics")["metrics"]
                        if metric["name"] == "Nodes")

        page.evaluate("replaceTranslatedRows()")
        baseline = collect_nodes()
        for _ in range(5):
            page.evaluate("replaceTranslatedRows()")
        retained = collect_nodes() - baseline
        if retained > 50:
            raise RuntimeError(f"Localization retained {retained} DOM nodes from removed rows")
        page.evaluate("verifyTranslations()")
        result = page.locator("h1").inner_text()
        if result != "Localization passed":
            raise RuntimeError(result)
        print(f"{result}. {retained:g} additional DOM nodes after 1000 replaced translated rows.", flush=True)
        page.close()
        for harness in ("test-output-route", "test-media-timeline", "test-mic-signals", "test-mic-sample-player"):
            path = subprocess.check_output(["node", f"ui/tools/{harness}.mjs"], cwd=ROOT, text=True).strip()
            page = browser.new_page()
            page.goto(Path(path).as_uri())
            page.wait_for_function(" /passed|failed/i.test(document.querySelector('h1').textContent)")
            result = page.locator("h1").inner_text()
            if "failed" in result.lower():
                raise RuntimeError(result)
            print(result, flush=True)
            page.close()
        browser.close()


if __name__ == "__main__":
    main()
