// Основа: рисование, лента отмен, признак несохранённой работы.
//
// Это опорные проверки. Дальше в эту версию переносятся правки из C#-версии, и каждая из
// них рискует задеть общее состояние - здесь описано то, что задевать нельзя.

const { test, expect } = require('@playwright/test');
const { openApp, isWhite, isNear } = require('./harness');

test('карандаш оставляет след там, где провели мышью', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('pencil');
  await app.setColor('#ff0000');
  await app.setSize(8);

  expect(isWhite(await app.pixel(300, 300))).toBe(true);
  await app.drag(200, 300, 400, 300);

  const mid = await app.pixel(300, 300);
  expect(isNear(mid, 255, 0, 0)).toBe(true);
  // Мимо линии бумага осталась бумагой.
  expect(isWhite(await app.pixel(300, 380))).toBe(true);
});

test('один штрих - одна запись в ленте', async ({ page }) => {
  const app = await openApp(page);
  const before = await app.history();

  await app.pickTool('pencil');
  await app.drag(100, 100, 300, 200);

  const after = await app.history();
  expect(after.labels.length).toBe(before.labels.length + 1);
  expect(after.index).toBe(before.index + 1);
});

test('отмена возвращает холст до пикселя, повтор - обратно', async ({ page }) => {
  const app = await openApp(page);
  const clean = await app.fingerprint();

  await app.pickTool('pencil');
  await app.setColor('#0000ff');
  await app.setSize(10);
  await app.drag(150, 150, 500, 400);
  const drawn = await app.fingerprint();
  expect(drawn).not.toBe(clean);

  await app.undo();
  expect(await app.fingerprint()).toBe(clean);

  await app.redo();
  expect(await app.fingerprint()).toBe(drawn);
});

test('отмена дальше начала ленты ничего не портит', async ({ page }) => {
  const app = await openApp(page);
  const clean = await app.fingerprint();

  await app.pickTool('pencil');
  await app.drag(200, 200, 300, 300);
  await app.undo();
  await app.undo();
  await app.undo();

  expect(await app.fingerprint()).toBe(clean);
});

test('заливка красит замкнутую область', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('fill');
  await app.setColor('#00a000');
  await app.clickAt(450, 300);

  expect(isNear(await app.pixel(450, 300), 0, 160, 0)).toBe(true);
  expect(isNear(await app.pixel(50, 50), 0, 160, 0)).toBe(true);
});

test('ластик возвращает бумагу', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('pencil');
  await app.setColor('#000000');
  await app.setSize(14);
  await app.drag(200, 300, 600, 300);
  expect(isWhite(await app.pixel(400, 300))).toBe(false);

  await app.pickTool('eraser');
  await app.page.evaluate(() => { state.eraserSize = 60; syncSizeForTool(); });
  await app.drag(200, 300, 600, 300);
  expect(isWhite(await app.pixel(400, 300))).toBe(true);
});

test('смена инструмента не пишет в ленту', async ({ page }) => {
  const app = await openApp(page);
  const before = await app.history();
  for (const t of ['brush', 'rect', 'ellipse', 'line', 'eraser', 'pencil']) {
    await app.pickTool(t);
  }
  expect((await app.history()).labels.length).toBe(before.labels.length);
});

test('свежий документ не считается несохранённым, а после штриха - считается', async ({ page }) => {
  const app = await openApp(page);
  expect(await app.isDirty()).toBe(false);

  await app.pickTool('pencil');
  await app.drag(100, 100, 200, 200);
  expect(await app.isDirty()).toBe(true);

  await app.undo();
  expect(await app.isDirty()).toBe(false);
});

test('пипетка забирает цвет с холста и не пишет в ленту', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('pencil');
  await app.setColor('#ff00ff');
  await app.setSize(20);
  await app.drag(300, 300, 500, 300);

  const before = await app.history();
  await app.pickTool('picker');
  await app.clickAt(400, 300);

  expect((await app.page.evaluate(() => state.color)).toLowerCase()).toBe('#ff00ff');
  expect((await app.history()).labels.length).toBe(before.labels.length);
});

test('прямоугольник рисуется от точки нажатия до точки отпускания', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('rect');
  await app.setColor('#000000');
  await app.setSize(4);
  await app.drag(200, 200, 600, 400);

  // Углы на месте, середина пустая (режим по умолчанию - контур).
  expect(isWhite(await app.pixel(200, 200))).toBe(false);
  expect(isWhite(await app.pixel(600, 400))).toBe(false);
  expect(isWhite(await app.pixel(400, 300))).toBe(true);
});
