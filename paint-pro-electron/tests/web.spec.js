// Отдельный файл для браузера.
//
// Приложение живёт в одном HTML: стили и скрипт внутри, снаружи не подгружается ничего.
// Значит его можно просто переслать - открыть двойным щелчком на любом устройстве. Но
// «можно» надо проверять: в браузере нет моста в Electron, и всё, что ходило через него -
// открытие файла, сохранение, вопрос при закрытии, номер версии, - обязано иметь запасной
// путь. Здесь проверяется собранный файл из dist, а не исходник: пересылают именно его.

const { test, expect } = require('@playwright/test');
const fs = require('fs');
const path = require('path');
const { pathToFileURL } = require('url');

const VERSION = require('../package.json').version;
const WEB_FILE = path.join(__dirname, '..', 'dist', `paint-pro-${VERSION}-web.html`);

/** Открыть собранный файл БЕЗ подмены моста: ровно так его увидит чужой браузер. */
async function openWeb(page) {
  expect(fs.existsSync(WEB_FILE), 'соберите файл: npm run build:web').toBe(true);
  const errors = [];
  page.on('pageerror', (e) => errors.push(String(e)));
  await page.goto(pathToFileURL(WEB_FILE).href);
  await page.waitForFunction(() => typeof state !== 'undefined' && !!document.getElementById('canvas'));
  return errors;
}

test('файл собран и открывается сам по себе', async ({ page }) => {
  const errors = await openWeb(page);
  expect(errors, 'страница ругается при загрузке: ' + errors.join('; ')).toEqual([]);
  expect(await page.evaluate(() => !!window.electronAPI), 'моста быть не должно').toBe(false);
  expect(await page.evaluate(() => [canvas.width, canvas.height])).toEqual([900, 600]);
});

test('в собранный файл вписан номер версии', async ({ page }) => {
  await openWeb(page);
  await page.waitForTimeout(200);
  await expect(page.locator('#app-version')).toContainText(VERSION);
  expect(await page.title()).toContain(VERSION);
});

test('рисование работает без Electron', async ({ page }) => {
  await openWeb(page);
  const box = await page.locator('#canvas').boundingBox();
  await page.click('[data-tool="pencil"]');
  await page.evaluate(() => { state.size = 24; syncSizeForTool(); setColor('#ff0000'); });
  await page.mouse.move(box.x + 100, box.y + 100);
  await page.mouse.down();
  await page.mouse.move(box.x + 500, box.y + 300, { steps: 8 });
  await page.mouse.up();
  await page.waitForFunction(() => !state.restoring);

  const painted = await page.evaluate(() => {
    const d = ctx.getImageData(0, 0, canvas.width, canvas.height).data;
    for (let i = 0; i < d.length; i += 4) if (d[i] > 180 && d[i + 1] < 100) return true;
    return false;
  });
  expect(painted, 'штрих не лёг').toBe(true);
  expect((await page.evaluate(() => state.history.length))).toBeGreaterThan(1);
});

test('слои, лента и темы работают без Electron', async ({ page }) => {
  await openWeb(page);
  expect(await page.evaluate(() => { addLayer(); return state.layers.length; })).toBe(2);
  expect(await page.evaluate(() => setTheme('formal'))).toBe('formal');
  expect(await page.evaluate(() => THEMES.length)).toBe(5);

  await page.evaluate(() => undo());
  await page.waitForFunction(() => !state.restoring);
  expect(await page.evaluate(() => state.layers.length)).toBe(1);
});

test('открытие файла в браузере идёт через поле выбора, а не через мост', async ({ page }) => {
  // Нативного диалога тут нет. Проверяем ПОВЕДЕНИЕ, а не имя функции: она живёт в своей
  // области видимости и снаружи не видна, а вот запрос файла у браузера виден.
  const errors = await openWeb(page);
  const asked = page.waitForEvent('filechooser', { timeout: 5000 })
    .then(() => true).catch(() => false);
  await page.keyboard.press('Control+o');
  expect(await asked, 'браузер не спросил файл: запасного пути открытия нет').toBe(true);
  expect(errors, 'Ctrl+O уронил страницу: ' + errors.join('; ')).toEqual([]);
});

test('сохранение в браузере идёт скачиванием', async ({ page }) => {
  await openWeb(page);
  const box = await page.locator('#canvas').boundingBox();
  await page.click('[data-tool="pencil"]');
  await page.mouse.move(box.x + 60, box.y + 60);
  await page.mouse.down();
  await page.mouse.move(box.x + 300, box.y + 200, { steps: 5 });
  await page.mouse.up();
  await page.waitForFunction(() => !state.restoring);

  // Скачивание в файловом протоколе браузер может не начать, поэтому проверяем сам путь:
  // сохранение обязано отчитаться успехом и снять признак несохранённой работы.
  const ok = await page.evaluate(() => saveCanvas(false));
  expect(ok, 'сохранение в браузере не отработало').toBe(true);
  expect(await page.evaluate(() => isDirty())).toBe(false);
});

test('файл ничего не тянет из сети', async ({ page }) => {
  // Иначе «перекинул на другое устройство» работало бы только с интернетом.
  const external = [];
  page.on('request', (r) => {
    const u = r.url();
    if (!u.startsWith('file:') && !u.startsWith('data:') && !u.startsWith('blob:')) external.push(u);
  });
  await openWeb(page);
  await page.waitForTimeout(400);
  expect(external, 'файл тянет что-то извне: ' + external.join(', ')).toEqual([]);
});

test('каждая сборка кладёт в dist и файл для браузера', () => {
  // Установщик, portable и один .html - три вида одной программы, и в dist после любой
  // сборки должны лежать все, какие она делает, плюс браузерный. npm сам зовёт pre<имя>.
  const scripts = require('../package.json').scripts;
  const builds = Object.keys(scripts).filter((k) => /electron-builder/.test(scripts[k]));
  expect(builds.length).toBeGreaterThan(0);
  for (const name of builds) {
    expect(scripts['pre' + name], `перед «npm run ${name}» не собирается браузерный файл`).toBe('node tools/build-web.js');
  }
});
