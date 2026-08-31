// Отказ без слов - худший вид отказа: нажал, ничего не произошло, понять почему нельзя.
// Здесь перечислены все места, где действие законно не делает ничего и обязано сказать
// об этом. В WPF-версии то же самое разбиралось в 1.19.0-1.21.0.

const { test, expect } = require('@playwright/test');
const { openApp } = require('./harness');

test('копирование без выделения объясняется', async ({ page }) => {
  const app = await openApp(page);
  await app.page.evaluate(() => copySelection());
  expect(await app.hintText()).toMatch(/выдел/i);
});

test('вырезание без выделения объясняется своими словами', async ({ page }) => {
  const app = await openApp(page);
  await app.page.evaluate(() => cutSelection());
  const text = await app.hintText();
  expect(text).toMatch(/выдел/i);
  expect(text).toMatch(/резать/i);   // «Вырезать нечего», а не «копировать нечего»
});

test('вставка по пустому буферу объясняется', async ({ page }) => {
  const app = await openApp(page);
  await app.page.evaluate(() => pasteFromClipboard());
  expect(await app.hintText()).toMatch(/буфер/i);
});

test('удаление без выделения объясняется', async ({ page }) => {
  const app = await openApp(page);
  await page.keyboard.press('Delete');
  await app.page.waitForTimeout(50);
  expect(await app.hintText()).toMatch(/выдел/i);
});

test('заливка прозрачными чернилами объясняется и не пишет в ленту', async ({ page }) => {
  const app = await openApp(page);
  const n = (await app.history()).labels.length;
  await app.pickTool('fill');
  await app.setColor('#ff0000');
  await app.setOpacity(0);
  await app.clickAt(450, 300);
  expect(await app.hintText()).toMatch(/прозрачн/i);
  expect((await app.history()).labels.length).toBe(n);
});

test('увеличение, упёршееся в потолок, объясняется', async ({ page }) => {
  const app = await openApp(page);
  for (let i = 0; i < 40; i++) await app.page.evaluate(() => zoomIn());
  expect(await app.hintText()).toMatch(/предел/i);
});

test('уменьшение, упёршееся в пол, объясняется', async ({ page }) => {
  const app = await openApp(page);
  for (let i = 0; i < 40; i++) await app.page.evaluate(() => zoomOut());
  expect(await app.hintText()).toMatch(/предел/i);
});

test('слишком маленькая рамка выделения не пропадает молча', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('select');
  await app.drag(300, 300, 302, 302);
  expect(await app.hintText()).toMatch(/мала|шире/i);
});

test('слишком маленькая рамка обрезки не пропадает молча', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('crop');
  await app.drag(300, 300, 302, 302);
  expect(await app.hintText()).toMatch(/мала|шире/i);
});

test('слишком маленькая рамка полигона не пропадает молча', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('quad');
  await app.drag(300, 300, 310, 310);
  expect(await app.hintText()).toMatch(/20/);
});

test('поворот хоткеем без поднятого объекта объясняется', async ({ page }) => {
  const app = await openApp(page);
  await page.keyboard.press('BracketRight');
  await app.page.waitForTimeout(50);
  expect(await app.hintText()).toMatch(/поднят/i);
});

// ─────────── И обратная сторона: там, где действие СРАБОТАЛО, подсказки быть не должно ───────────

test('удавшееся копирование ничего не объясняет', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('pencil');
  await app.setSize(14);
  await app.drag(200, 200, 500, 400);
  await app.pickTool('select');
  await app.drag(180, 180, 520, 420);
  await app.page.evaluate(() => copySelection());
  expect(await app.hintText()).toBe('');
});

test('обычная заливка ничего не объясняет', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('fill');
  await app.setColor('#00aa00');
  await app.setOpacity(1);
  await app.clickAt(450, 300);
  expect(await app.hintText()).toBe('');
});

test('нормальная рамка выделения ничего не объясняет', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('select');
  await app.drag(200, 200, 500, 400);
  expect(await app.hintText()).toBe('');
});

test('масштаб внутри границ ничего не объясняет', async ({ page }) => {
  const app = await openApp(page);
  await app.page.evaluate(() => { zoomIn(); zoomOut(); });
  expect(await app.hintText()).toBe('');
});
