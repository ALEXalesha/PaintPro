// Кружок размера кисти и толщина инструментов (1.18.0, 1.19.0).
//
// Кружок был курсором-картинкой, а у курсоров Chromium потолок: он зажимался в 96 точек
// и на кисти 300 px показывал треть настоящего размера. И сам размер значил у инструментов
// разное: карандаш рисовал вдвое тоньше заданного, маркер - в полтора раза толще.
// В 1.18.0 кружок стал элементом поверх холста - и стал отставать от указателя на кадр-два.
// С 1.19.0 кружок до 120 точек - в курсоре-картинке (без задержки), больше - элементом,
// а системный указатель над ним прячется и крестик рисуется там же.

const { test, expect } = require('@playwright/test');
const { openApp, isWhite } = require('./harness');

async function hoverAt(app, x, y) {
  const p = await app.toScreen(x, y);
  await app.page.mouse.move(p.x, p.y);
  await app.page.evaluate(() => new Promise(requestAnimationFrame));
  return p;
}

/** Что сейчас показывает размер: курсор-картинку или элемент, и какой диаметр. */
const shown = (app) => app.page.evaluate(() => {
  const el = document.getElementById('brush-ring');
  const r = el.getBoundingClientRect();
  const cursor = canvas.style.cursor;
  const m = /circle cx='[\d.]+' cy='[\d.]+' r='([\d.]+)'/.exec(decodeURIComponent(cursor));
  return {
    element: getComputedStyle(el).display !== 'none',
    w: r.width, h: r.height, cx: r.left + r.width / 2, cy: r.top + r.height / 2,
    cursor,
    cursorD: m ? parseFloat(m[1]) * 2 : null,
  };
});

for (const tool of ['pencil', 'brush', 'marker', 'eraser']) {
  for (const size of [4, 60, 120]) {
    test(`${tool} ${size} px: кружок в курсоре, диаметр - размер, элемента нет`, async ({ page }) => {
      const app = await openApp(page);
      await app.pickTool(tool);
      await page.evaluate((v) => setToolSize(v), size);
      await hoverAt(app, 450, 300);
      const s = await shown(app);
      expect(s.element).toBe(false);
      expect(Math.abs(s.cursorD - size)).toBeLessThanOrEqual(0.5);
    });
  }
  for (const size of [121, 150, 300]) {
    test(`${tool} ${size} px: кружок элементом того же диаметра, указатель спрятан`, async ({ page }) => {
      const app = await openApp(page);
      await app.pickTool(tool);
      await page.evaluate((v) => setToolSize(v), size);
      await hoverAt(app, 450, 300);
      const s = await shown(app);
      expect(s.element).toBe(true);
      expect(s.cursor).toBe('none');
      expect(Math.abs(s.w - size)).toBeLessThanOrEqual(1);
      expect(Math.abs(s.h - size)).toBeLessThanOrEqual(1);
    });
  }
}

test('большой кружок: крестик нарисован в центре элемента', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('brush');
  await page.evaluate(() => setToolSize(200));
  await hoverAt(app, 450, 300);
  const cross = await page.evaluate(() => {
    const el = document.getElementById('brush-ring');
    const b = getComputedStyle(el, '::before'), a = getComputedStyle(el, '::after');
    return { bw: b.width, bh: b.height, aw: a.width, ah: a.height, content: b.content };
  });
  expect(cross).toEqual({ bw: '11px', bh: '1px', aw: '1px', ah: '11px', content: '""' });
});

test('курсор-картинка не больше потолка Chromium (128 точек)', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('brush');
  await page.evaluate(() => setToolSize(120));
  const size = await page.evaluate(() => {
    const m = /width='(\d+)'/.exec(decodeURIComponent(canvas.style.cursor));
    return m ? parseInt(m[1], 10) : -1;
  });
  expect(size).toBeGreaterThan(0);
  expect(size).toBeLessThanOrEqual(128);
});

for (const zoom of [0.5, 2]) {
  test(`масштаб ${zoom}: диаметр в экранных точках - размер, умноженный на масштаб`, async ({ page }) => {
    const app = await openApp(page);
    await app.pickTool('brush');
    await page.evaluate((z) => { state.zoom = z; applyZoom(); }, zoom);
    await page.evaluate(() => setToolSize(100));
    await hoverAt(app, 300, 200);
    const s = await shown(app);
    const d = s.element ? s.w : s.cursorD;
    expect(Math.abs(d - 100 * zoom)).toBeLessThanOrEqual(1);
  });
}

test('большой кружок стоит центром на указателе', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('brush');
  await page.evaluate(() => setToolSize(200));
  const p = await hoverAt(app, 333, 222);
  const s = await shown(app);
  expect(Math.abs(s.cx - p.x)).toBeLessThanOrEqual(1);
  expect(Math.abs(s.cy - p.y)).toBeLessThanOrEqual(1);
});

test('большой кружок идёт за мышью', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('brush');
  await page.evaluate(() => setToolSize(200));
  await hoverAt(app, 100, 100);
  const a = await shown(app);
  const p = await hoverAt(app, 700, 500);
  const b = await shown(app);
  expect(b.cx - a.cx).toBeGreaterThan(500);
  expect(Math.abs(b.cy - p.y)).toBeLessThanOrEqual(1);
});

test('колесо меняет кружок сразу, и через потолок в 120 - тоже', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('brush');
  await page.evaluate(() => setToolSize(110));
  await hoverAt(app, 450, 300);
  expect((await shown(app)).element).toBe(false);
  for (let i = 0; i < 3; i++) await page.mouse.wheel(0, -120);
  await page.evaluate(() => new Promise(requestAnimationFrame));
  const size = await page.evaluate(() => state.size);
  expect(size).toBeGreaterThan(120);
  const s = await shown(app);
  expect(s.element).toBe(true);
  expect(Math.abs(s.w - size)).toBeLessThanOrEqual(1);
});

test('мышь ушла с холста - большого кружка нет', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('brush');
  await page.evaluate(() => setToolSize(200));
  await hoverAt(app, 450, 300);
  expect((await shown(app)).element).toBe(true);
  await page.mouse.move(2, 2);
  await page.evaluate(() => new Promise(requestAnimationFrame));
  expect((await shown(app)).element).toBe(false);
});

for (const tool of ['select', 'fill', 'picker', 'text', 'hand']) {
  test(`${tool}: у инструмента без размера кружка нет`, async ({ page }) => {
    const app = await openApp(page);
    await app.pickTool('brush');
    await page.evaluate(() => setToolSize(200));
    await app.pickTool(tool);
    await hoverAt(app, 450, 300);
    const s = await shown(app);
    expect(s.element).toBe(false);
    expect(s.cursorD).toBe(null);
  });
}

test('кружок не мешает рисовать: мышь проходит сквозь него', async ({ page }) => {
  await openApp(page);
  expect(await page.evaluate(() => getComputedStyle(document.getElementById('brush-ring')).pointerEvents)).toBe('none');
});

// ─────────── настоящая толщина ───────────

async function bandHalfWidth(app, tool, size) {
  await app.pickTool(tool);
  if (tool !== 'eraser') await app.setColor('#000000');
  await app.page.evaluate((v) => setToolSize(v), size);
  await app.drag(150, 300, 750, 300);
  // Докуда вверх от линии дотянулся мазок на середине: самая дальняя закрашенная (или
  // стёртая) точка. Не «до первого просвета»: у кисти с 1.19.0 между ворсинками просветы.
  return app.page.evaluate(() => {
    const d = ctx.getImageData(450, 0, 1, canvas.height).data;
    let n = 0;
    for (let y = 300; y >= 0; y--) {
      const i = y * 4;
      const white = d[i] > 240 && d[i + 1] > 240 && d[i + 2] > 240;
      if (!white) n = 300 - y + 1;
    }
    return n;
  });
}

for (const tool of ['pencil', 'brush', 'marker']) {
  test(`${tool}: полоса шириной в заданный размер`, async ({ page }) => {
    const app = await openApp(page);
    const half = await bandHalfWidth(app, tool, 60);
    // У кисти край неровный (ворсинки), но не уже 0.42 размера и не шире самого размера.
    expect(half, `половина полосы ${half}`).toBeGreaterThanOrEqual(tool === 'brush' ? 25 : 29);
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
