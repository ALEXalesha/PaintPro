// Жест мышью: что его продолжает и что заканчивает.
//
// Рамка выделения и её ручки лежат ПОВЕРХ холста отдельными элементами, а не внутри него.
// Пока тянешь ручку, курсор то и дело оказывается над ней - для canvas это ровно тот же
// mouseleave, что и уход за край. Пока на mouseleave висел onUp, ресайз срывался, стоило
// отклониться от линии на пару пикселей: вести мышь приходилось идеально по траектории.
// Тот же mouseleave обрывал и обычный мазок на краю холста, хотя кнопка оставалась
// зажатой, - в WPF-версии мышь на время жеста захватывается и там ничего не обрывалось.

const { test, expect } = require('@playwright/test');
const { openApp, isWhite } = require('./harness');

const isBlack = (p) => p[0] < 90 && p[1] < 90 && p[2] < 90;

/** Нарисовать штрих и выделить его рамкой - обычная подготовка к ресайзу. */
async function select(app) {
  await app.pickTool('pencil');
  await app.setColor('#000000');
  await app.setSize(14);
  await app.drag(200, 200, 500, 400);
  await app.pickTool('select');
  await app.drag(180, 180, 520, 420);
}

/** Экранный центр ручки рамки выделения. */
async function handle(app, which) {
  const b = await app.page.locator(`.selection-overlay .resize-handle.${which}`).boundingBox();
  return { x: b.x + b.width / 2, y: b.y + b.height / 2 };
}

const size = (app) => app.page.evaluate(() => state.floating && {
  w: Math.round(state.floating.w), h: Math.round(state.floating.h),
});

// ─────────── ресайз идёт за мышью, а не по идеальной линии ───────────

test('ресайз углом переживает отклонение от траектории', async ({ page }) => {
  const app = await openApp(page);
  await select(app);
  const h = await handle(app, 'se');
  await page.mouse.move(h.x, h.y);
  await page.mouse.down();
  // Уводим курсор с ручки в другую сторону и только потом тянем куда надо.
  await page.mouse.move(h.x + 40, h.y - 30, { steps: 10 });
  await page.mouse.move(h.x + 120, h.y + 90, { steps: 20 });
  const box = await size(app);
  await page.mouse.up();
  expect(box.w).toBeGreaterThan(400);
  expect(box.h).toBeGreaterThan(300);
});

test('боковая ручка тянется, даже когда курсор с неё сошёл', async ({ page }) => {
  const app = await openApp(page);
  await select(app);
  // У ручки «w» курсор сходит с неё при любом вертикальном движении: сама она
  // ездит только по горизонтали.
  const h = await handle(app, 'w');
  await page.mouse.move(h.x, h.y);
  await page.mouse.down();
  await page.mouse.move(h.x - 150, h.y + 20, { steps: 20 });
  const box = await size(app);
  await page.mouse.up();
  expect(box.w).toBeGreaterThan(400);
});

test('отпускание вне холста закрывает ресайз, не оставляя его висеть', async ({ page }) => {
  const app = await openApp(page);
  await select(app);
  const h = await handle(app, 'se');
  await page.mouse.move(h.x, h.y);
  await page.mouse.down();
  await page.mouse.move(1590, 990, { steps: 20 });
  await page.mouse.up();
  await app.settle();
  expect(await page.evaluate(() => state.resizingFloating)).toBe(false);
  expect(await page.evaluate(() => state.resizeHandle)).toBe(null);
});

test('перетаскивание объекта не срывается на собственных ручках', async ({ page }) => {
  const app = await openApp(page);
  await select(app);
  await app.clickAt(350, 300);
  const from = await app.toScreen(350, 300);
  await page.mouse.move(from.x, from.y);
  await page.mouse.down();
  // Путь вверх проходит ровно над верхней ручкой рамки.
  await page.mouse.move(from.x + 30, from.y - 130, { steps: 15 });
  await page.mouse.move(from.x + 120, from.y + 40, { steps: 15 });
  const moved = await page.evaluate(() => Math.round(state.floating.x));
  await page.mouse.up();
  expect(moved).toBeGreaterThan(230);
});

// ─────────── мазок кончается кнопкой, а не краем холста ───────────

test('мазок продолжается, когда курсор вышел за холст и вернулся', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('pencil');
  await app.setColor('#000000');
  await app.setSize(14);
  const start = await app.toScreen(400, 300);
  const box = await app.canvasBox();
  await page.mouse.move(start.x, start.y);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width + 40, start.y, { steps: 10 });
  const back = await app.toScreen(400, 380);
  await page.mouse.move(back.x, back.y, { steps: 10 });
  await page.mouse.up();
  await app.settle();
  expect(isBlack(await app.pixel(400, 380))).toBe(true);
  // И это по-прежнему ОДИН штрих, а не два обрубка.
  expect((await app.history()).labels).toEqual(['Исходное состояние', 'Штрих']);
});

test('отпускание за краем холста закрывает мазок одной записью', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('pencil');
  await app.setColor('#000000');
  await app.setSize(14);
  const start = await app.toScreen(400, 300);
  const box = await app.canvasBox();
  await page.mouse.move(start.x, start.y);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width + 60, box.y + box.height + 60, { steps: 10 });
  await page.mouse.up();
  await app.settle();
  expect(await page.evaluate(() => state.drawing)).toBe(false);
  expect((await app.history()).labels.length).toBe(2);
});

test('фигура, доведённая за край, кладётся один раз', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('rect');
  await app.setColor('#000000');
  await app.setSize(6);
  const start = await app.toScreen(300, 300);
  const box = await app.canvasBox();
  await page.mouse.move(start.x, start.y);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width + 50, box.y + box.height + 50, { steps: 10 });
  await page.mouse.up();
  await app.settle();
  expect((await app.history()).labels).toEqual(['Исходное состояние', 'Фигура']);
});

test('ластик за краем и обратно стирает без разрыва', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('pencil');
  await app.setColor('#000000');
  await app.setSize(30);
  await app.drag(100, 300, 800, 300);
  await app.pickTool('eraser');
  await app.setSize(30);
  const start = await app.toScreen(200, 300);
  const box = await app.canvasBox();
  await page.mouse.move(start.x, start.y);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width + 40, start.y, { steps: 8 });
  const back = await app.toScreen(700, 300);
  await page.mouse.move(back.x, back.y, { steps: 8 });
  await page.mouse.up();
  await app.settle();
  expect(isWhite(await app.pixel(700, 300))).toBe(true);
  expect((await app.history()).labels.length).toBe(3);
});

// ─────────── жест, отпускания которого мы не увидели ───────────
//
// Кнопку можно отпустить за краем окна или под чужим окном, вышедшим вперёд: mouseup
// тогда не приходит совсем. Раньше такой жест частично закрывал уход курсора с холста;
// теперь, когда уход больше ничего не закрывает, нужен явный подбор - тот же, что в
// WPF-версии делает Skia.LostMouseCapture.

test('движение с отпущенной кнопкой закрывает зависший жест', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('pencil');
  await app.setColor('#000000');
  await app.setSize(14);
  const start = await app.toScreen(300, 300);
  await page.mouse.move(start.x, start.y);
  await page.mouse.down();
  await page.mouse.move(start.x + 100, start.y + 100, { steps: 5 });

  // Гасим mouseup по дороге - ровно то, что видит страница при отпускании за окном.
  await page.evaluate(() => {
    window.__swallow = (ev) => ev.stopImmediatePropagation();
    document.addEventListener('mouseup', window.__swallow, true);
  });
  await page.mouse.up();
  expect(await page.evaluate(() => state.drawing)).toBe(true);

  await page.evaluate(() => document.removeEventListener('mouseup', window.__swallow, true));
  await page.mouse.move(start.x + 160, start.y + 160, { steps: 3 });
  await app.settle();
  expect(await page.evaluate(() => state.drawing)).toBe(false);
  expect((await app.history()).labels.length).toBe(2);
});

test('потеря фокуса окном закрывает мазок одной записью', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('pencil');
  await app.setColor('#000000');
  await app.setSize(14);
  const start = await app.toScreen(300, 300);
  await page.mouse.move(start.x, start.y);
  await page.mouse.down();
  await page.mouse.move(start.x + 100, start.y + 100, { steps: 5 });
  await page.evaluate(() => window.dispatchEvent(new Event('blur')));
  await app.settle();
  expect(await page.evaluate(() => state.drawing)).toBe(false);
  expect((await app.history()).labels.length).toBe(2);
  await page.mouse.up();
});

test('потеря фокуса окном закрывает и ресайз', async ({ page }) => {
  const app = await openApp(page);
  await select(app);
  const h = await handle(app, 'se');
  await page.mouse.move(h.x, h.y);
  await page.mouse.down();
  await page.mouse.move(h.x + 80, h.y + 80, { steps: 10 });
  await page.evaluate(() => window.dispatchEvent(new Event('blur')));
  expect(await page.evaluate(() => state.resizingFloating)).toBe(false);
  await page.mouse.up();
});
