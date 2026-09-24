const { test, expect } = require('@playwright/test');
const { launchApp, closeApp } = require('./harness');

test('приложение запускается и показывает свою версию', async () => {
  const { app, win } = await launchApp();
  try {
    const version = await app.evaluate(async ({ app }) => app.getVersion());
    expect(version).toBe(require('../package.json').version);
    await expect(win.locator('#app-version')).toContainText(version);
    expect(await win.evaluate(() => [canvas.width, canvas.height])).toEqual([900, 600]);
  } finally {
    await closeApp(app);
  }
});

test('окно открывается там и того размера, каким его закрыли', async () => {
  const fs = require('fs');
  const os = require('os');
  const path = require('path');
  const dataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'paintpro-data-'));
  const target = { x: 120, y: 90, width: 1100, height: 760 };
  try {
    const first = await launchApp([], { dataDir });
    await first.app.evaluate(({ BrowserWindow }, b) => BrowserWindow.getAllWindows()[0].setBounds(b), target);
    await closeApp(first.app);
    const saved = JSON.parse(fs.readFileSync(path.join(dataDir, 'window-state.json'), 'utf8'));
    expect(saved).toMatchObject({ ...target, maximized: false });

    const second = await launchApp([], { dataDir });
    try {
      const b = await second.app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0].getBounds());
      expect(b).toEqual(target);
    } finally {
      await closeApp(second.app);
    }
  } finally {
    fs.rmSync(dataDir, { recursive: true, force: true });
  }
});

test('каждый файл, который main.js берёт через require, попадает в сборку', async () => {
  const pkg = require('../package.json');
  const src = require('fs').readFileSync(require('path').join(__dirname, '..', 'main.js'), 'utf8');
  const local = [...src.matchAll(/require\('\.\/([^']+)'\)/g)].map(m => m[1].endsWith('.js') ? m[1] : m[1] + '.js');
  expect(local.length).toBeGreaterThan(0);
  for (const f of local) expect(pkg.build.files, f).toContain(f);
});

test('самые узкие панели на самом маленьком окне: ничего не вылезает, и с полосой прокрутки тоже', async () => {
  // В быстром наборе браузер прячет полосы прокрутки, и они не отнимают ширину. Здесь -
  // настоящее окно с настоящей полосой в 10 px.
  const { app, win } = await launchApp();
  try {
    await app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0].setContentSize(900, 600));
    await win.evaluate(() => { panelPref.left = 1; panelPref.right = 1; applyPanels(); });
    await win.evaluate(() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(() => r()))));
    const r = await win.evaluate(() => {
      const check = (sel) => {
        const panel = document.querySelector(sel);
        const cs = getComputedStyle(panel);
        const box = panel.getBoundingClientRect();
        const left = box.left + panel.clientLeft + parseFloat(cs.paddingLeft);
        const right = box.left + panel.clientLeft + panel.clientWidth - parseFloat(cs.paddingRight);
        const out = [];
        for (const el of panel.querySelectorAll('*')) {
          const b = el.getBoundingClientRect();
          if (!b.width || !b.height) continue;
          // Выбранный образец намеренно увеличен (transform) и выступает в поле панели.
          if (el.matches('.color-swatch.active')) continue;
          if (el.closest('.history-list, .layers-list') && el !== el.closest('.history-list, .layers-list')) continue;
          if (b.left < left - 0.5 || b.right > right + 0.5) out.push((el.id || el.className || el.tagName) + ' ' + Math.round(b.left - left) + '/' + Math.round(right - b.right));
        }
        return { scroll: panel.offsetWidth - panel.clientWidth - 2, out };
      };
      return { side: check('.sidebar'), tools: check('.tools-panel'),
               widths: [document.querySelector('.tools-panel').offsetWidth, document.querySelector('.sidebar').offsetWidth] };
    });
    expect(r.widths).toEqual([88, 260]);
    expect(r.side.scroll, 'у правой панели нет полосы прокрутки - проверка ничего не значит').toBeGreaterThan(0);
    expect(r.side.out).toEqual([]);
    expect(r.tools.out).toEqual([]);
  } finally {
    await closeApp(app);
  }
});
