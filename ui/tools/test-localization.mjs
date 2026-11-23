import { build } from 'esbuild';
import { mkdtemp, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const ui = fileURLToPath(new URL('../', import.meta.url));
const result = await build({
  stdin: {
    resolveDir: ui,
    contents: `
      import { bindAttr, bindText, setLabels } from './src/i18n';
      const title = document.querySelector('h1');
      const visible = document.createElement('button');
      const cached = document.createElement('button');
      for (const button of [visible, cached]) {
        button.append(bindText('common.back'));
        bindAttr(button, 'aria-label', 'common.back');
      }
      document.body.append(visible);

      window.replaceTranslatedRows = () => {
        const group = document.createElement('div');
        document.body.append(group);
        for (let i = 0; i < 200; i++) {
          const row = document.createElement('button');
          row.append(bindText('common.back'));
          bindAttr(row, 'aria-label', 'common.back');
          group.replaceChildren(row);
        }
        group.remove();
      };

      window.verifyTranslations = () => {
        setLabels({ 'common.back': 'Return' }, 'test', false, () => {});
        document.body.append(cached);
        for (const button of [visible, cached]) {
          if (button.textContent !== 'Return' || button.getAttribute('aria-label') !== 'Return') {
            title.textContent = 'Localization failed: existing controls did not update';
            return;
          }
        }
        title.textContent = 'Localization passed';
      };
    `,
  },
  bundle: true,
  format: 'iife',
  write: false,
});

const directory = await mkdtemp(join(tmpdir(), 'parity-localization-'));
const output = join(directory, 'index.html');
await writeFile(output, `<!doctype html><meta charset="utf-8"><h1>Localization pending</h1>
<script>${result.outputFiles[0].text.replaceAll(/<\/script/gi, '<\\/script')}</script>`);
console.log(output);
