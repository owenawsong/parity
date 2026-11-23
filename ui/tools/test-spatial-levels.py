"""Run the spatial player's audio regressions with Edge and Python Playwright installed."""
from pathlib import Path
import subprocess

from playwright.sync_api import sync_playwright


def main():
    ui = Path(__file__).resolve().parents[1]
    bundle = subprocess.check_output([
        "node", "--input-type=module", "-e",
        """import {build} from 'esbuild';
        const result = await build({entryPoints:['src/components/spatialStage.ts'],bundle:true,
          format:'iife',globalName:'pairside',loader:{'.ogg':'binary'},write:false});
        process.stdout.write(result.outputFiles[0].text);""",
    ], cwd=ui).decode()
    with sync_playwright() as runner:
        browser = runner.chromium.launch(channel="msedge", args=["--mute-audio", "--autoplay-policy=no-user-gesture-required"])
        try:
            page = browser.new_page()
            page.add_script_tag(content=bundle)
            print(page.evaluate((ui / "tools/spatial-level-check.js").read_text(encoding="utf8")))
            page.close()
            page = browser.new_page()
            page.add_script_tag(content=bundle)
            print(page.evaluate((ui / "tools/spatial-file-check.js").read_text(encoding="utf8")))
        finally:
            browser.close()


if __name__ == "__main__":
    main()
