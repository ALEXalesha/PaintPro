// Размер, изменённый колесом ПОСРЕДИ штриха (1.17.0).
//
// Кисть, карандаш и маркер брали толщину один раз, на нажатии, и рисовали ею до
// отпускания: крутишь колесо, кружок курсора растёт, а линия идёт прежней. Ластик на
// экране толщину менял, но в запись ленты уходил один размер - и пересборка ленты
// (выключатель правки) рисовала такой штрих иначе, чем он был нарисован. Теперь толщина
// берётся на каждом движении и записывается у каждой точки.

const { test, expect } = require('@playwright/test');
const { openApp, isWhite } = require('./harness');

/** Штрих слева направо на высоте y; посередине колесо крутится, пока размер не станет target. */
async function strokeWithWheel(app, y, target, x1 = 100, x2 = 700) {
  const page = app.page;
  const a = await app.toScreen(x1, y);
  await page.mouse.move(a.x, a.y);
  await page.mouse.down();
  const mid = (x1 + x2) / 2;
  for (let x = x1 + 10; x <= mid; x += 10) {
    const p = await app.toScreen(x, y);
    await page.mouse.move(p.x, p.y);
  }
  const start = await page.evaluate(() => state.size);
  const up = target > start;
  for (let i = 0; i < 80; i++) {
    const now = await page.evaluate(() => state.size);
    if (up ? now >= target : now <= target) break;
    await page.mouse.wheel(0, up ? -120 : 120);
    await page.evaluate(() => new Promise(requestAnimationFrame));
  }
  for (let x = mid + 10; x <= x2; x += 10) {
    const p = await app.toScreen(x, y);
    await page.mouse.move(p.x, p.y);
  }
  await page.mouse.up();
  await app.settle();
}

const inked = async (app, x, y) => !isWhite(await app.pixel(x, y));

for (const tool of ['brush', 'marker']) {
  test(`${tool}: колесо посреди штриха - дальше линия новой толщины, без отпускания кнопки`, async ({ page }) => {
    const app = await openApp(page);
    await app.pickTool(tool);
    await app.setColor('#000000');
    await app.setSize(4);
    await strokeWithWheel(app, 300, 40);
    expect(await page.evaluate(() => state.size)).toBeGreaterThanOrEqual(40);

    expect(await inked(app, 250, 300)).toBe(true);
    expect(await inked(app, 250, 312), 'первая половина должна остаться тонкой').toBe(false);
    expect(await inked(app, 600, 310), 'вторая половина должна стать толстой').toBe(true);
  });
}

test('pencil: колесо посреди штриха меняет толщину карандаша', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('pencil');
  await app.setColor('#000000');
  await app.setSize(4);
  await strokeWithWheel(app, 300, 40);
  const size = await page.evaluate(() => state.size);
  expect(size).toBeGreaterThanOrEqual(40);
  expect(await inked(app, 250, 306)).toBe(false);
  expect(await inked(app, 600, 300 + Math.floor(size * 0.25) - 1)).toBe(true);
});

test('колесо вниз посреди штриха - линия становится тоньше', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('brush');
  await app.setColor('#000000');
  await app.setSize(40);
  await strokeWithWheel(app, 300, 4);
  expect(await page.evaluate(() => state.size)).toBeLessThanOrEqual(4);
  expect(await inked(app, 250, 315)).toBe(true);
  expect(await inked(app, 600, 315)).toBe(false);
});

test('ластик: колесо посреди штриха, и пересборка ленты рисует так же', async ({ page }) => {
  const app = await openApp(page);
  // Чёрная подложка прямоугольником с заливкой, потом ластик.
  await app.pickTool('rect');
  await app.setColor('#000000');
  await page.evaluate(() => { state.fillMode = 'solid'; });
  await app.drag(50, 200, 850, 400);
  await app.pickTool('eraser');
  await page.evaluate(() => setToolSize(4));
  await strokeWithWheel(app, 300, 40);
  expect(isWhite(await app.pixel(600, 310)), 'толстая часть стёрла').toBe(true);
  expect(isWhite(await app.pixel(250, 312)), 'тонкая часть не должна стирать так широко').toBe(false);

  const before = await app.fingerprint();
  // Выключить и включить прямоугольник: ластик поверх пересобирается из записи.
  await page.evaluate(() => setEntryEnabled(1, false));
  await app.settle();
  await page.evaluate(() => setEntryEnabled(1, true));
  await app.settle();
  expect(await app.fingerprint(), 'пересборка ластика разошлась с оригиналом').toBe(before);
});

for (const tool of ['brush', 'pencil', 'marker']) {
  test(`${tool}: пересборка ленты повторяет штрих с колесом до пикселя`, async ({ page }) => {
    const app = await openApp(page);
    await app.pickTool('pencil');
    await app.setColor('#ff0000');
    await app.setSize(10);
    await app.drag(100, 150, 700, 150);
    await app.pickTool(tool);
    await app.setColor('#0000ff');
    await app.setSize(4);
    await strokeWithWheel(app, 300, 40);
    const before = await app.fingerprint();

    await page.evaluate(() => setEntryEnabled(1, false));
    await app.settle();
    await page.evaluate(() => setEntryEnabled(1, true));
    await app.settle();
    expect(await app.fingerprint(), 'штрих с колесом пересобрался иначе').toBe(before);
  });
}

test('штрих с колесом посередине - одна запись в ленте, отмена снимает его целиком', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('brush');
  await app.setColor('#000000');
  await app.setSize(4);
  const n = (await app.history()).labels.length;
  await strokeWithWheel(app, 300, 40);
  expect((await app.history()).labels.length).toBe(n + 1);
  await app.undo();
  expect(await inked(app, 250, 300)).toBe(false);
  expect(await inked(app, 600, 310)).toBe(false);
  await app.redo();
  expect(await inked(app, 600, 310)).toBe(true);
});

test('запись штриха хранит размер у каждой точки', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('brush');
  await app.setSize(4);
  await strokeWithWheel(app, 300, 40);
  const rec = await page.evaluate(() => state.history[state.history.length - 1].replay);
  expect(rec.sizes.length * 2).toBe(rec.points.length);
  expect(rec.sizes[0]).toBe(4);
  expect(rec.sizes[rec.sizes.length - 1]).toBeGreaterThan(4);
});

test('следующий штрих начинается уже с нового размера', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('brush');
  await app.setColor('#000000');
  await app.setSize(4);
  await strokeWithWheel(app, 300, 40);
  await app.drag(100, 500, 400, 500);
  expect(await inked(app, 250, 510)).toBe(true);
});

// ─────────── Предел размера: 300 вместо 100 (1.17.0) ───────────

test('предел размера - 300, и колесо до него доходит', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('brush');
  expect(await page.evaluate(() => MAX_TOOL_SIZE)).toBe(300);
  await page.evaluate(() => setToolSize(100));
  const p = await app.toScreen(400, 300);
  await page.mouse.move(p.x, p.y);
  for (let i = 0; i < 60; i++) await page.mouse.wheel(0, -120);
  await app.settle();
  expect(await page.evaluate(() => state.size)).toBe(300);
});

test('кисть на 300 рисует полосу шириной 300', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('brush');
  await app.setColor('#000000');
  await page.evaluate(() => setToolSize(300));
  await app.drag(150, 300, 750, 300);
  expect(await inked(app, 450, 300 - 145)).toBe(true);
  expect(await inked(app, 450, 300 + 145)).toBe(true);
  expect(await inked(app, 450, 300 - 156)).toBe(false);
  expect(await inked(app, 450, 300 + 156)).toBe(false);
});

test('ластик на 300 тоже до 300', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('eraser');
  await page.evaluate(() => setToolSize(1000));
  expect(await page.evaluate(() => state.eraserSize)).toBe(300);
});
