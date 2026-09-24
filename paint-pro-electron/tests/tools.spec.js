// Инструменты: что рисуют, чем отличаются, во что не лезут.

const { test, expect } = require('@playwright/test');
const { openApp, isWhite, isNear } = require('./harness');

test('каждая фигура что-то рисует и даёт ровно одну запись', async ({ page }) => {
  const app = await openApp(page);
  await app.setColor('#000000');
  await app.setSize(5);
  for (const tool of ['line', 'rect', 'ellipse', 'triangle', 'star', 'arrow', 'heart']) {
    const n = (await app.history()).index;
    const before = await app.fingerprint();
    await app.pickTool(tool);
    await app.drag(200, 150, 700, 450);
    expect(await app.fingerprint(), tool).not.toBe(before);
    expect((await app.history()).index, tool).toBe(n + 1);
    await app.undo();
  }
});

test('прямоугольник справа налево и снизу вверх рисуется тот же', async ({ page }) => {
  const app = await openApp(page);
  await app.setColor('#000000');
  await app.setSize(4);
  await app.pickTool('rect');
  await app.drag(200, 200, 600, 400);
  const forward = await app.fingerprint();
  await app.undo();
  await app.drag(600, 400, 200, 200);
  expect(await app.fingerprint()).toBe(forward);
});

test('сплошная фигура закрашена внутри, контурная - нет', async ({ page }) => {
  const app = await openApp(page);
  await app.setColor('#000000');
  await app.setSize(4);
  await app.pickTool('rect');

  await app.page.evaluate(() => setFillMode('solid'));
  await app.drag(200, 200, 600, 400);
  expect(isWhite(await app.pixel(400, 300))).toBe(false);

  await app.undo();
  await app.page.evaluate(() => setFillMode('outline'));
  await app.drag(200, 200, 600, 400);
  expect(isWhite(await app.pixel(400, 300))).toBe(true);
});

test('неизвестный режим заливки не сбивает текущий', async ({ page }) => {
  const app = await openApp(page);
  await app.page.evaluate(() => setFillMode('solid'));
  await app.page.evaluate(() => setFillMode('чепуха'));
  expect(await app.page.evaluate(() => state.fillMode)).toBe('solid');
});

test('заливка не вылезает за замкнутый контур', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('rect');
  await app.setColor('#000000');
  await app.setSize(6);
  await app.drag(200, 150, 700, 450);

  await app.pickTool('fill');
  await app.setColor('#ff0000');
  await app.clickAt(450, 300);

  expect(isNear(await app.pixel(450, 300), 255, 0, 0)).toBe(true);
  expect(isWhite(await app.pixel(100, 100))).toBe(true);
  expect(isWhite(await app.pixel(850, 550))).toBe(true);
});

test('заливка не перетекает через сплошную черту', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('pencil');
  await app.setColor('#000000');
  await app.setSize(20);
  await app.drag(2, 300, 897, 300);

  await app.pickTool('fill');
  await app.setColor('#0000ff');
  await app.clickAt(450, 100);
  expect(isNear(await app.pixel(450, 100), 0, 0, 255)).toBe(true);
  expect(isNear(await app.pixel(450, 500), 0, 0, 255)).toBe(false);
});

test('ластик не зависит от выбранного цвета', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('pencil');
  await app.setColor('#000000');
  await app.setSize(20);
  await app.drag(200, 300, 700, 300);

  await app.pickTool('eraser');
  await app.setColor('#ff0000');
  await app.page.evaluate(() => { state.eraserSize = 60; syncSizeForTool(); });
  await app.drag(200, 300, 700, 300);
  expect(isWhite(await app.pixel(450, 300))).toBe(true);
});

test('маркер за два прохода кладёт плотнее, чем за один', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('marker');
  await app.setColor('#000000');
  await app.setSize(20);
  await app.setOpacity(0.3);
  await app.drag(200, 200, 700, 200);
  const one = (await app.pixel(450, 200))[0];
  await app.drag(200, 400, 700, 400);
  await app.drag(200, 400, 700, 400);
  const two = (await app.pixel(450, 400))[0];
  expect(two).toBeLessThan(one);
});

test('полупрозрачная кисть кладёт цвет светлее сплошного', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('brush');
  await app.setColor('#000000');
  await app.setSize(30);
  await app.setOpacity(0.3);
  await app.drag(200, 300, 600, 300);
  const p = await app.pixel(400, 300);
  expect(p[0]).toBeGreaterThan(60);
  expect(p[0]).toBeLessThan(240);
});

test('рука не оставляет следов и не пишет в ленту', async ({ page }) => {
  const app = await openApp(page);
  const before = await app.fingerprint();
  const n = (await app.history()).labels.length;
  await app.pickTool('hand');
  await app.drag(200, 200, 600, 400);
  expect(await app.fingerprint()).toBe(before);
  expect((await app.history()).labels.length).toBe(n);
});

test('пипетка берёт именно тот цвет, что под курсором', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('fill');
  await app.setColor('#3366cc');
  await app.clickAt(450, 300);

  await app.setColor('#000000');
  await app.pickTool('picker');
  await app.clickAt(450, 300);
  expect((await app.page.evaluate(() => state.color)).toLowerCase()).toBe('#3366cc');
});

test('пипетка за пределами холста не роняет приложение', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('picker');
  await app.clickAt(-50, -50);
  expect(await app.page.evaluate(() => typeof state.color)).toBe('string');
});

test('щелчок по образцу палитры меняет цвет', async ({ page }) => {
  const app = await openApp(page);
  const hex = await app.page.evaluate(() => {
    document.querySelectorAll('#palette .color-swatch')[colors.indexOf('#ff0000')].click();
    return state.color;
  });
  expect(hex.toLowerCase()).toBe('#ff0000');
});

test('толщина мазка не уходит в ноль или в минус', async ({ page }) => {
  const app = await openApp(page);
  const w = await app.page.evaluate(() =>
    [-5, 0, 1, 1000].map((s) => strokeWidthFor('pencil', s)));
  expect(w.every((x) => x >= 1)).toBe(true);
});

test('огромный размер кисти не роняет приложение', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('brush');
  await app.setSize(500);
  await app.drag(400, 300, 500, 300);
  expect(await app.page.evaluate(() => canvas.width)).toBe(900);
});

// ─────────── Текст ───────────

test('набранный текст ложится на холст одной записью и отменяется', async ({ page }) => {
  const app = await openApp(page);
  const n = (await app.history()).index;
  const before = await app.fingerprint();

  await app.pickTool('text');
  await app.clickAt(300, 300);
  await app.page.evaluate(() => { textEditor.innerText = 'Привет'; });
  await app.page.evaluate(() => commitText());
  await app.settle();

  expect(await app.fingerprint()).not.toBe(before);
  expect((await app.history()).index).toBe(n + 1);

  await app.undo();
  expect(await app.fingerprint()).toBe(before);
});

test('пустой текстовый редактор ничего не пишет в ленту', async ({ page }) => {
  const app = await openApp(page);
  const n = (await app.history()).labels.length;
  await app.pickTool('text');
  await app.clickAt(300, 300);
  await page.keyboard.press('Escape');
  await app.settle();
  expect((await app.history()).labels.length).toBe(n);
  expect(await app.isDirty()).toBe(false);
});

test('незакрытый непустой редактор считается несохранённой работой', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('text');
  await app.clickAt(300, 300);
  expect(await app.isDirty()).toBe(false);
  await app.page.evaluate(() => { textEditor.innerText = 'Черновик'; });
  expect(await app.isDirty()).toBe(true);
});

// ─────────── Горячие клавиши ───────────

test('Ctrl+Z и Ctrl+Y ходят по ленте', async ({ page }) => {
  const app = await openApp(page);
  const clean = await app.fingerprint();
  await app.pickTool('pencil');
  await app.setSize(10);
  await app.drag(200, 200, 400, 400);
  const drawn = await app.fingerprint();

  await page.keyboard.press('Control+z');
  await app.settle();
  expect(await app.fingerprint()).toBe(clean);

  await page.keyboard.press('Control+y');
  await app.settle();
  expect(await app.fingerprint()).toBe(drawn);
});

test('Ctrl+Shift+Z тоже повторяет', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('pencil');
  await app.setSize(10);
  await app.drag(200, 200, 400, 400);
  const drawn = await app.fingerprint();
  await page.keyboard.press('Control+z');
  await app.settle();
  await page.keyboard.press('Control+Shift+z');
  await app.settle();
  expect(await app.fingerprint()).toBe(drawn);
});

test('хоткеи инструментов переключают инструмент', async ({ page }) => {
  const app = await openApp(page);
  for (const pair of [['p', 'pencil'], ['b', 'brush'], ['e', 'eraser'], ['g', 'fill']]) {
    await page.keyboard.press(pair[0]);
    expect(await app.page.evaluate(() => state.tool), pair[0]).toBe(pair[1]);
  }
});
