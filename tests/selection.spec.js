// Выделение, буфер обмена, плавающий объект и сеанс работы.

const { test, expect } = require('@playwright/test');
const { openApp } = require('./harness');

/** Нарисовать заметный штрих, чтобы было что выделять. */
async function draw(app) {
  await app.pickTool('pencil');
  await app.setColor('#000000');
  await app.setSize(14);
  await app.drag(200, 200, 500, 400);
}

test('выделение без перемещения не пачкает документ', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('select');
  await app.drag(200, 200, 400, 400);
  expect(await app.isDirty()).toBe(false);
  expect((await app.history()).labels.length).toBe(1);
});

test('«выделить всё» не пачкает документ', async ({ page }) => {
  const app = await openApp(page);
  await app.page.evaluate(() => selectAll());
  await app.settle();
  expect(await app.isDirty()).toBe(false);
});

test('выделение справа налево охватывает ту же область', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('select');
  await app.drag(600, 400, 200, 200);
  const sel = await app.page.evaluate(() => state.selection && {
    x: Math.round(state.selection.x), y: Math.round(state.selection.y),
    w: Math.round(state.selection.w), h: Math.round(state.selection.h),
  });
  expect(sel).not.toBeNull();
  expect(sel.w).toBeGreaterThan(0);
  expect(sel.h).toBeGreaterThan(0);
  expect(sel.x).toBeLessThanOrEqual(210);
  expect(sel.y).toBeLessThanOrEqual(210);
});

test('выделение, вытянутое за край, не выходит за холст', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('select');
  await app.drag(700, 450, 1400, 900);
  const sel = await app.page.evaluate(() => state.selection && { ...state.selection });
  expect(sel).not.toBeNull();
  expect(sel.x + sel.w).toBeLessThanOrEqual(900);
  expect(sel.y + sel.h).toBeLessThanOrEqual(600);
});

test('выделение и Escape оставляют холст нетронутым', async ({ page }) => {
  const app = await openApp(page);
  await draw(app);
  await app.page.evaluate(() => { state.savedHistoryIndex = state.historyIndex; });
  const before = await app.fingerprint();
  const n = (await app.history()).labels.length;

  await app.pickTool('select');
  await app.drag(250, 250, 450, 380);
  await page.keyboard.press('Escape');
  await app.settle();

  expect(await app.fingerprint()).toBe(before);
  expect((await app.history()).labels.length).toBe(n);
  expect(await app.isDirty()).toBe(false);
});

test('подъём выделения без сдвига не пачкает документ', async ({ page }) => {
  // Именно сдвинутый пикап, а не сам факт подъёма: клик внутрь выделения поднимает
  // пиксели, но холст остаётся прежним до пикселя.
  const app = await openApp(page);
  await draw(app);
  await app.page.evaluate(() => { state.savedHistoryIndex = state.historyIndex; });

  await app.pickTool('select');
  await app.drag(250, 250, 450, 380);
  await app.clickAt(350, 300);
  expect(await app.isDirty()).toBe(false);
});

test('перемещение выделения можно отменить до пикселя', async ({ page }) => {
  const app = await openApp(page);
  await draw(app);
  const before = await app.fingerprint();

  await app.pickTool('select');
  await app.drag(180, 180, 520, 420);
  await app.drag(300, 250, 600, 450);
  await page.keyboard.press('Enter');
  await app.settle();
  expect(await app.fingerprint()).not.toBe(before);

  await app.undo();
  expect(await app.fingerprint()).toBe(before);
});

test('копирование без выделения не пишет в ленту и не роняет', async ({ page }) => {
  const app = await openApp(page);
  const n = (await app.history()).labels.length;
  await app.page.evaluate(() => copySelection());
  expect((await app.history()).labels.length).toBe(n);
  expect(await app.isDirty()).toBe(false);
});

test('вырезание без выделения не меняет холст', async ({ page }) => {
  const app = await openApp(page);
  const before = await app.fingerprint();
  const n = (await app.history()).labels.length;
  await app.page.evaluate(() => cutSelection());
  await app.settle();
  expect(await app.fingerprint()).toBe(before);
  expect((await app.history()).labels.length).toBe(n);
});

test('вставка кладёт плавающий объект, отмена после фиксации возвращает холст', async ({ page }) => {
  const app = await openApp(page);
  await draw(app);
  await app.pickTool('select');
  await app.drag(180, 180, 520, 420);
  await app.page.evaluate(() => copySelection());

  const before = await app.fingerprint();
  await app.page.evaluate(() => pasteFromClipboard());
  await app.settle();
  expect(await app.page.evaluate(() => !!state.floating)).toBe(true);

  await app.drag(300, 250, 650, 480);
  await page.keyboard.press('Enter');
  await app.settle();
  expect(await app.fingerprint()).not.toBe(before);

  await app.undo();
  expect(await app.fingerprint()).toBe(before);
});

test('полигон: отказ возвращает холст и не пачкает документ', async ({ page }) => {
  const app = await openApp(page);
  await draw(app);
  await app.page.evaluate(() => { state.savedHistoryIndex = state.historyIndex; });
  const before = await app.fingerprint();

  await app.pickTool('quad');
  await app.drag(180, 180, 520, 420);
  await page.keyboard.press('Escape');
  await app.settle();

  expect(await app.fingerprint()).toBe(before);
  expect(await app.isDirty()).toBe(false);
});

// ─────────── Сеанс: сохранение и закрытие ───────────

test('чистый документ закрывается без вопроса', async ({ page }) => {
  const app = await openApp(page);
  await app.page.evaluate(() => handleCloseRequest());
  expect(await app.page.evaluate(() => window.__calls.map((c) => c.name)))
    .toContain('confirmClose');
});

test('документ со штрихом молча не закрывается', async ({ page }) => {
  const app = await openApp(page);
  await draw(app);
  // Заглушка отвечает «отмена»: окно закрываться не должно.
  await app.page.evaluate(() => handleCloseRequest());
  await app.page.waitForTimeout(50);
  expect(await app.page.evaluate(() => window.__calls.map((c) => c.name)))
    .not.toContain('confirmClose');
});

test('отказ от выбора пути не считается сохранением', async ({ page }) => {
  const app = await openApp(page);
  await draw(app);
  expect(await app.isDirty()).toBe(true);
  await app.page.evaluate(() => saveCanvas(true));
  await app.page.waitForTimeout(60);
  expect(await app.isDirty()).toBe(true);
});
