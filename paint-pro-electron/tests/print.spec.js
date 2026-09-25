// Печать - только лист (1.20.0). Раньше Ctrl+P печатал всё окно: меню, панели и
// инструменты, а картинка шла мелкой частью посередине.

const { test, expect } = require('@playwright/test');
const { openApp } = require('./harness');

/** Подменить window.print: настоящий открыл бы системный диалог и повесил проверку. */
async function stubPrint(page) {
  await page.evaluate(() => {
    window.__printed = [];
    window.print = () => {
      window.dispatchEvent(new Event('beforeprint'));
      const img = document.querySelector('#print-sheet img');
      window.__printed.push(img ? { src: img.src, w: img.naturalWidth, h: img.naturalHeight } : null);
    };
  });
}

async function paintSomething(app) {
  await app.pickTool('rect');
  await app.setColor('#ff0000');
  await app.page.evaluate(() => { state.fillMode = 'solid'; });
  await app.drag(100, 100, 300, 250);
}

/** Пиксель картинки из data:-адреса. */
async function pixelOf(page, src, x, y) {
  return page.evaluate(async ([s, px, py]) => {
    const img = new Image();
    img.src = s;
    await img.decode();
    const c = document.createElement('canvas');
    c.width = img.naturalWidth; c.height = img.naturalHeight;
    const g = c.getContext('2d');
    g.drawImage(img, 0, 0);
    return Array.from(g.getImageData(px, py, 1, 1).data);
  }, [src, x, y]);
}

test('Ctrl+P печатает, и на листе - картинка холста в его размер', async ({ page }) => {
  const app = await openApp(page);
  await paintSomething(app);
  await stubPrint(page);
  await page.keyboard.press('Control+p');
  await page.waitForFunction(() => window.__printed.length === 1);
  const [p] = await page.evaluate(() => window.__printed);
  expect(p).not.toBeNull();
  expect([p.w, p.h]).toEqual([900, 600]);
  expect(await pixelOf(page, p.src, 200, 180)).toEqual([255, 0, 0, 255]);
  expect(await pixelOf(page, p.src, 600, 400)).toEqual([255, 255, 255, 255]);
});

test('пункт «Файл → Печать…» делает то же', async ({ page }) => {
  const app = await openApp(page);
  await stubPrint(page);
  await page.click('.mb-menu[data-menu="file"] .mb-item');
  await page.locator('.mb-dd-item', { hasText: 'Печать' }).click();
  await page.waitForFunction(() => window.__printed.length === 1);
  expect(await page.locator('.mb-dd-item', { hasText: 'Печать' }).locator('.kb').textContent()).toBe('Ctrl P');
});

test('при печати видна только картинка листа, интерфейса нет', async ({ page }) => {
  const app = await openApp(page);
  await paintSomething(app);
  await page.evaluate(() => preparePrintSheet());
  await page.emulateMedia({ media: 'print' });
  const shown = await page.evaluate(() => {
    const visible = (el) => !!el && getComputedStyle(el).display !== 'none' && el.getBoundingClientRect().width > 0;
    const others = [...document.body.children].filter((el) => el.id !== 'print-sheet');
    return {
      sheet: visible(document.getElementById('print-sheet')),
      img: visible(document.querySelector('#print-sheet img')),
      othersVisible: others.filter((el) => getComputedStyle(el).display !== 'none').map((el) => el.id || el.className || el.tagName),
      menubar: visible(document.querySelector('.mb-menu')),
      tools: visible(document.querySelector('[data-tool="pencil"]')),
      canvas: visible(document.getElementById('canvas')),
    };
  });
  expect(shown.sheet).toBe(true);
  expect(shown.img).toBe(true);
  expect(shown.othersVisible).toEqual([]);
  expect(shown.menubar).toBe(false);
  expect(shown.tools).toBe(false);
  expect(shown.canvas).toBe(false);
});

test('на экране листа печати не видно', async ({ page }) => {
  const app = await openApp(page);
  await page.evaluate(() => preparePrintSheet());
  const d = await page.evaluate(() => getComputedStyle(document.getElementById('print-sheet')).display);
  expect(d).toBe('none');
  expect(await page.locator('.mb-menu').first().isVisible()).toBe(true);
});

test('PDF из печати - одна страница с картинкой, без единого текста интерфейса', async ({ page, browserName }) => {
  test.skip(browserName !== 'chromium', 'page.pdf есть только в Chromium');
  const app = await openApp(page);
  await paintSomething(app);
  await page.evaluate(() => preparePrintSheet());
  await page.evaluate(() => document.querySelector('#print-sheet img').decode());
  const pdf = (await page.pdf({ format: 'A4', printBackground: true })).toString('latin1');
  const pages = (pdf.match(/\/Type\s*\/Page[^s]/g) || []).length;
  expect(pages).toBe(1);
  expect(pdf).toMatch(/\/Subtype\s*\/Image/);
  // Текст интерфейса лёг бы в PDF со шрифтом; у картинки шрифтов нет.
  expect(pdf).not.toMatch(/\/Font/);
});

test('поднятый объект печатается там, где лежит, а документ не меняется', async ({ page }) => {
  const app = await openApp(page);
  await paintSomething(app);
  await app.pickTool('select');
  await app.drag(90, 90, 310, 260);
  await app.drag(200, 180, 600, 380);   // объект в руках, сдвинут
  expect(await page.evaluate(() => !!state.floating)).toBe(true);
  const before = await app.history();
  await stubPrint(page);
  await page.keyboard.press('Control+p');
  await page.waitForFunction(() => window.__printed.length === 1);
  const [p] = await page.evaluate(() => window.__printed);
  expect(await pixelOf(page, p.src, 600, 380)).toEqual([255, 0, 0, 255]);
  expect(await pixelOf(page, p.src, 200, 180)).toEqual([255, 255, 255, 255]);
  // Объект так и остался в руках, лента та же.
  expect(await page.evaluate(() => !!state.floating)).toBe(true);
  expect(await app.history()).toEqual(before);
  expect(await app.isDirty()).toBe(true);
});

test('несколько слоёв и прозрачность слоя - на листе так же, как на экране', async ({ page }) => {
  const app = await openApp(page);
  await paintSomething(app);
  await page.evaluate(() => { addLayer(); });
  await app.pickTool('rect');
  await app.setColor('#0000ff');
  await app.drag(250, 200, 450, 350);
  const screen = await page.evaluate(() => document.getElementById('canvas').toDataURL());
  await stubPrint(page);
  await page.keyboard.press('Control+p');
  await page.waitForFunction(() => window.__printed.length === 1);
  const [p] = await page.evaluate(() => window.__printed);
  for (const [x, y] of [[150, 150], [280, 230], [400, 300], [700, 500]]) {
    expect(await pixelOf(page, p.src, x, y)).toEqual(await pixelOf(page, screen, x, y));
  }
});

test('печать из меню браузера (без нас) тоже готовит лист', async ({ page }) => {
  const app = await openApp(page);
  await paintSomething(app);
  await page.evaluate(() => window.dispatchEvent(new Event('beforeprint')));
  expect(await page.locator('#print-sheet img').count()).toBe(1);
});

test('после печати картинка листа убирается - память не держим', async ({ page }) => {
  const app = await openApp(page);
  await stubPrint(page);
  await page.keyboard.press('Control+p');
  await page.waitForFunction(() => window.__printed.length === 1);
  await page.evaluate(() => window.dispatchEvent(new Event('afterprint')));
  expect(await page.locator('#print-sheet img').count()).toBe(0);
});

test('Ctrl+P в поле ввода не перехватывается', async ({ page }) => {
  await openApp(page);
  await stubPrint(page);
  await page.focus('#width-input');
  await page.keyboard.press('Control+p');
  await page.waitForTimeout(100);
  expect(await page.evaluate(() => window.__printed.length)).toBe(0);
});
