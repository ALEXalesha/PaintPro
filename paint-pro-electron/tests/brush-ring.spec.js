// Кружок размера кисти и толщина инструментов (1.18.0).
//
// Кружок был курсором-картинкой, а у курсоров Chromium потолок: он зажимался в 96 точек
// и на кисти 300 px показывал треть настоящего размера. И сам размер значил у инструментов
// разное: карандаш рисовал вдвое тоньше заданного, маркер - в полтора раза толще, и «300 px»
// у ластика и у карандаша были разными полосами. Теперь размер - настоящая толщина у всех,
// а кружок - элемент поверх холста любого размера.

const { test, expect } = require('@playwright/test');
const { openApp, isWhite } = require('./harness');

async function hoverAt(app, x, y) {
  const p = await app.toScreen(x, y);
  await app.page.mouse.move(p.x, p.y);
  await app.page.evaluate(() => new Promise(requestAnimationFrame));
  return p;
}

const ring = (app) => app.page.evaluate(() => {
  const el = document.getElementById('brush-ring');
  const r = el.getBoundingClientRect();
  return { shown: getComputedStyle(el).display !== 'none', w: r.width, h: r.height, cx: r.left + r.width / 2, cy: r.top + r.height / 2 };
});

for (const tool of ['pencil', 'brush', 'marker', 'eraser']) {
  for (const size of [4, 60, 150, 300]) {
    test(`${tool} ${size} px: кружок того же диаметра, что и размер`, async ({ page }) => {
      const app = await openApp(page);
      await app.pickTool(tool);
      await page.evaluate((v) => setToolSize(v), size);
      await hoverAt(app, 450, 300);
      const r = await ring(app);
      expect(r.shown).toBe(true);
      expect(Math.abs(r.w - size)).toBeLessThanOrEqual(1);
      expect(Math.abs(r.h - size)).toBeLessThanOrEqual(1);
    });
  }
}

for (const zoom of [0.5, 2]) {
  test(`масштаб ${zoom}: кружок в экранных точках - размер, умноженный на масштаб`, async ({ page }) => {
    const app = await openApp(page);
    await app.pickTool('brush');
    await page.evaluate((z) => { state.zoom = z; applyZoom(); }, zoom);
    await page.evaluate(() => setToolSize(100));
    await hoverAt(app, 300, 200);
    const r = await ring(app);
    expect(Math.abs(r.w - 100 * zoom)).toBeLessThanOrEqual(1);
  });
}

test('кружок стоит центром на курсоре', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('brush');
  await page.evaluate(() => setToolSize(80));
  const p = await hoverAt(app, 333, 222);
  const r = await ring(app);
  expect(Math.abs(r.cx - p.x)).toBeLessThanOrEqual(1);
  expect(Math.abs(r.cy - p.y)).toBeLessThanOrEqual(1);
});

test('кружок идёт за мышью', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('brush');
  await hoverAt(app, 100, 100);
  const a = await ring(app);
  const p = await hoverAt(app, 700, 500);
  const b = await ring(app);
  expect(b.cx - a.cx).toBeGreaterThan(500);
  expect(Math.abs(b.cy - p.y)).toBeLessThanOrEqual(1);
});

test('колесо меняет кружок сразу', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('brush');
  await page.evaluate(() => setToolSize(40));
  await hoverAt(app, 450, 300);
  await page.mouse.wheel(0, -120);
  await page.evaluate(() => new Promise(requestAnimationFrame));
  const size = await page.evaluate(() => state.size);
  expect(size).toBeGreaterThan(40);
  expect(Math.abs((await ring(app)).w - size)).toBeLessThanOrEqual(1);
});

test('мышь ушла с холста - кружка нет', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('brush');
  await hoverAt(app, 450, 300);
  expect((await ring(app)).shown).toBe(true);
  await page.mouse.move(2, 2);
  await page.evaluate(() => new Promise(requestAnimationFrame));
  expect((await ring(app)).shown).toBe(false);
});

for (const tool of ['select', 'fill', 'picker', 'text', 'hand']) {
  test(`${tool}: у инструмента без размера кружка нет`, async ({ page }) => {
    const app = await openApp(page);
    await app.pickTool(tool);
    await hoverAt(app, 450, 300);
    expect((await ring(app)).shown).toBe(false);
  });
}

test('кружок не мешает рисовать: мышь проходит сквозь него', async ({ page }) => {
  const app = await openApp(page);
  expect(await page.evaluate(() => getComputedStyle(document.getElementById('brush-ring')).pointerEvents)).toBe('none');
});

// ─────────── настоящая толщина ───────────

async function bandHalfWidth(app, tool, size) {
  await app.pickTool(tool);
  if (tool !== 'eraser') await app.setColor('#000000');
  await app.page.evaluate((v) => setToolSize(v), size);
  await app.drag(150, 300, 750, 300);
  // Сколько точек вверх от линии закрашено (или стёрто) на середине.
  return app.page.evaluate(() => {
    const d = ctx.getImageData(450, 0, 1, canvas.height).data;
    let n = 0;
    for (let y = 300; y >= 0; y--) {
      const i = y * 4;
      const white = d[i] > 240 && d[i + 1] > 240 && d[i + 2] > 240;
      if (white) break;
      n++;
    }
    return n;
  });
}

for (const tool of ['pencil', 'brush', 'marker']) {
  test(`${tool}: полоса шириной в заданный размер`, async ({ page }) => {
    const app = await openApp(page);
    const half = await bandHalfWidth(app, tool, 60);
    expect(half, `половина полосы ${half}`).toBeGreaterThanOrEqual(29);
    expect(half).toBeLessThanOrEqual(32);
  });
}

test('ластик 300 и кисть 300 - одна и та же полоса', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('rect');
  await app.setColor('#000000');
  await page.evaluate(() => { state.fillMode = 'solid'; });
  await app.drag(30, 30, 870, 570);
  await app.pickTool('eraser');
  await page.evaluate(() => setToolSize(300));
  await app.drag(150, 300, 750, 300);
  const erased = await page.evaluate(() => {
    const d = ctx.getImageData(450, 0, 1, canvas.height).data;
    let n = 0; for (let y = 40; y < 560; y++) if (d[y * 4] > 240) n++; return n;
  });
  expect(erased).toBeGreaterThanOrEqual(296);
  expect(erased).toBeLessThanOrEqual(304);
});

test('толщина не зависит от инструмента: одна функция на всех', async ({ page }) => {
  const app = await openApp(page);
  const w = await page.evaluate(() => ['pencil', 'brush', 'marker', 'eraser'].map((t) => strokeWidthFor(t, 77)));
  expect(w).toEqual([77, 77, 77, 77]);
});
