// Прямоугольный треугольник.
//
// От остальных фигур он отличается тем, что габарит жеста для него не нормализуется:
// прямой угол ставится ПО НАПРАВЛЕНИЮ движения мыши. Катеты выходят из точки старта,
// гипотенуза ложится ровно на линию, которую ведёт рука, - поэтому один и тот же
// прямоугольник даёт четыре разных треугольника, по одному на каждое направление.

const { test, expect } = require('@playwright/test');
const { openApp, isWhite } = require('./harness');

const isBlack = (p) => p[0] < 90 && p[1] < 90 && p[2] < 90;

async function shape(app, x1, y1, x2, y2, fill = false) {
  await app.pickTool('right-triangle');
  await app.setColor('#000000');
  await app.setSize(6);
  if (fill) await app.page.evaluate(() => { state.fillMode = 'solid'; });
  await app.drag(x1, y1, x2, y2);
}

test('катеты и гипотенуза ложатся по жесту', async ({ page }) => {
  const app = await openApp(page);
  await shape(app, 200, 200, 400, 400);
  expect(isBlack(await app.pixel(200, 300))).toBe(true);   // катет по x старта
  expect(isBlack(await app.pixel(300, 400))).toBe(true);   // катет по y конца
  expect(isBlack(await app.pixel(300, 300))).toBe(true);   // гипотенуза = линия жеста
  expect(isWhite(await app.pixel(380, 230))).toBe(true);   // за гипотенузой пусто
});

test('обратное направление переворачивает прямой угол', async ({ page }) => {
  const app = await openApp(page);
  await shape(app, 200, 400, 400, 200);
  expect(isBlack(await app.pixel(200, 300))).toBe(true);
  expect(isBlack(await app.pixel(300, 200))).toBe(true);
  expect(isWhite(await app.pixel(300, 390))).toBe(true);
});

test('заливка закрашивает внутренность, а не весь габарит', async ({ page }) => {
  const app = await openApp(page);
  await shape(app, 200, 200, 400, 400, true);
  expect(isBlack(await app.pixel(230, 380))).toBe(true);
  expect(isWhite(await app.pixel(380, 230))).toBe(true);
});

test('фигура не вылезает за прямоугольник жеста', async ({ page }) => {
  const app = await openApp(page);
  await shape(app, 300, 300, 500, 500);
  expect(isWhite(await app.pixel(280, 280))).toBe(true);
  expect(isWhite(await app.pixel(520, 520))).toBe(true);
});

test('отмена убирает фигуру целиком', async ({ page }) => {
  const app = await openApp(page);
  const clean = await app.fingerprint();
  await shape(app, 200, 200, 400, 400);
  await app.undo();
  expect(await app.fingerprint()).toBe(clean);
  expect(await app.isDirty()).toBe(false);
});

test('размер общий с остальными фигурами', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('rect');
  await app.setSize(11);
  await app.pickTool('right-triangle');
  expect(await page.evaluate(() => state.size)).toBe(11);
  expect(await page.evaluate(() => toolHasSize())).toBe(true);
});

test('инструмент называется своим именем в статусбаре', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('right-triangle');
  expect(await page.evaluate(() => document.getElementById('info-tool').textContent))
    .toBe('Прямоугольный треугольник');
});

test('прозрачность в ноль не кладёт фигуру', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('right-triangle');
  await app.setColor('#000000');
  await app.setSize(6);
  await app.setOpacity(0);
  await app.drag(200, 200, 400, 400);
  expect(isWhite(await app.pixel(200, 300))).toBe(true);
});
