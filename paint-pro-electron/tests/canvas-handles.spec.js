// Растягивание холста за любую сторону и любой угол.
//
// До 1.14.0 ручек было три: правая, нижняя и правый нижний угол. Они двигают только
// край - рисунок при этом стоит на месте. Левая и верхняя сторона устроены иначе: они
// двигают НАЧАЛО холста, и рисунок обязан поехать вместе с ним. Иначе «потянул влево»
// на экране выглядит как «рисунок прыгнул вправо» - жест сделал не то, что показывал.
//
// Поэтому здесь две породы проверок: арифметика смещения (её видно, не двигая мышь) и
// настоящее перетаскивание ручек с проверкой пикселей.

const { test, expect } = require('@playwright/test');
const { openApp, isWhite, isNear } = require('./harness');

const ALL = ['nw', 'n', 'ne', 'w', 'e', 'sw', 's', 'se'];

/** Посчитать геометрию, не двигая мышь. */
async function geom(app, dir, dm, dn, keepRatio = false) {
  return app.page.evaluate(([d, a, b, k]) =>
    canvasResizeGeometry(d, canvas.width, canvas.height, a, b, k), [dir, dm, dn, keepRatio]);
}

/** Потянуть ручку холста на dx/dy экранных пикселей. */
async function dragHandle(app, dir, dx, dy) {
  const box = await app.page.locator('#canvas-handle-' + dir).boundingBox();
  const x = box.x + box.width / 2, y = box.y + box.height / 2;
  await app.page.mouse.move(x, y);
  await app.page.mouse.down();
  await app.page.mouse.move(x + dx, y + dy, { steps: 6 });
  await app.page.mouse.up();
  await app.settle();
}

/** Поставить заметное красное пятно в точке документа. */
async function dot(app, x, y) {
  await app.page.evaluate(([px, py]) => {
    const g = state.layers[0].canvas.getContext('2d');
    g.fillStyle = '#ff0000';
    g.fillRect(px - 6, py - 6, 12, 12);
    composite();
  }, [x, y]);
}

const size = (app) => app.page.evaluate(() => [canvas.width, canvas.height]);

test('ручек восемь, у каждой своя', async ({ page }) => {
  const app = await openApp(page);
  for (const dir of ALL) {
    await expect(page.locator('#canvas-handle-' + dir), dir).toHaveCount(1);
    expect(await page.locator('#canvas-handle-' + dir).isVisible(), dir).toBe(true);
  }
});

test('правая и нижняя двигают край, рисунок стоит на месте', async ({ page }) => {
  const app = await openApp(page);
  expect(await geom(app, 'e', 100, 0)).toMatchObject({ w: 1000, h: 600, offX: 0, offY: 0 });
  expect(await geom(app, 's', 0, 100)).toMatchObject({ w: 900, h: 700, offX: 0, offY: 0 });
  expect(await geom(app, 'se', 100, 100)).toMatchObject({ w: 1000, h: 700, offX: 0, offY: 0 });
});

test('левая и верхняя двигают начало холста, рисунок едет с ним', async ({ page }) => {
  const app = await openApp(page);
  // Тянем влево - мышь идёт в минус, холст растёт, начало уезжает влево на те же 100.
  expect(await geom(app, 'w', -100, 0)).toMatchObject({ w: 1000, h: 600, offX: 100, offY: 0 });
  expect(await geom(app, 'n', 0, -100)).toMatchObject({ w: 900, h: 700, offX: 0, offY: 100 });
  expect(await geom(app, 'nw', -100, -100)).toMatchObject({ w: 1000, h: 700, offX: 100, offY: 100 });
});

test('сжатие слева срезает рисунок, а не отодвигает его', async ({ page }) => {
  const app = await openApp(page);
  expect(await geom(app, 'w', 100, 0)).toMatchObject({ w: 800, offX: -100 });
  expect(await geom(app, 'n', 0, 100)).toMatchObject({ h: 500, offY: -100 });
});

test('смещение считается после ограничений, а не до них', async ({ page }) => {
  // Иначе на упоре рисунок уехал бы дальше, чем выросла бумага, и часть его оказалась
  // бы за краем холста - потерянной без единого слова.
  const app = await openApp(page);
  const g = await geom(app, 'w', -1000000, 0);
  expect(g.offX, 'смещение больше прироста ширины').toBe(g.w - 900);
  expect(g.w).toBeLessThan(1000000);
});

test('холст не сжимается в точку', async ({ page }) => {
  const app = await openApp(page);
  for (const dir of ALL) {
    const g = await geom(app, dir, 100000, 100000);
    expect(g.w, dir).toBeGreaterThanOrEqual(50);
    expect(g.h, dir).toBeGreaterThanOrEqual(50);
  }
});

test('Shift на углу держит пропорции, на стороне не мешает', async ({ page }) => {
  const app = await openApp(page);
  const corner = await geom(app, 'se', 300, 0, true);
  expect(Math.abs(corner.w / corner.h - 900 / 600)).toBeLessThan(0.01);

  const side = await geom(app, 'e', 300, 0, true);
  expect(side, 'Shift на стороне не должен ничего держать').toMatchObject({ w: 1200, h: 600 });
});

test('перетаскивание правой ручки расширяет холст', async ({ page }) => {
  const app = await openApp(page);
  await dragHandle(app, 'e', 120, 0);
  const [w, h] = await size(app);
  expect(w).toBeGreaterThan(900);
  expect(h).toBe(600);
});

test('перетаскивание левой ручки уводит рисунок вправо ровно на прирост', async ({ page }) => {
  const app = await openApp(page);
  await dot(app, 20, 300);
  expect(isNear(await app.pixel(20, 300), 255, 0, 0), 'пятно не легло').toBe(true);

  await dragHandle(app, 'w', -140, 0);

  const [w] = await size(app);
  const grew = w - 900;
  expect(grew, 'холст не вырос').toBeGreaterThan(50);
  expect(isNear(await app.pixel(20 + grew, 300), 255, 0, 0), 'пятно не поехало вместе с краем').toBe(true);
  expect(isWhite(await app.pixel(10, 300)), 'новое место слева не белое').toBe(true);
});

test('перетаскивание верхней ручки уводит рисунок вниз ровно на прирост', async ({ page }) => {
  const app = await openApp(page);
  await dot(app, 400, 20);

  await dragHandle(app, 'n', 0, -140);

  const [, h] = await size(app);
  const grew = h - 600;
  expect(grew).toBeGreaterThan(50);
  expect(isNear(await app.pixel(400, 20 + grew), 255, 0, 0), 'пятно не поехало вниз').toBe(true);
  expect(isWhite(await app.pixel(400, 10)), 'новое место сверху не белое').toBe(true);
});

test('верхний левый угол двигает обе стороны сразу', async ({ page }) => {
  const app = await openApp(page);
  await dot(app, 20, 20);

  await dragHandle(app, 'nw', -120, -120);

  const [w, h] = await size(app);
  expect(w).toBeGreaterThan(900);
  expect(h).toBeGreaterThan(600);
  expect(isNear(await app.pixel(20 + (w - 900), 20 + (h - 600)), 255, 0, 0)).toBe(true);
});

test('растягивание пишет одну запись в ленту и отменяется целиком', async ({ page }) => {
  const app = await openApp(page);
  // Штрих настоящий, а не нарисованный мимо истории: отмена возвращает то, что в ленте.
  await app.pickTool('pencil');
  await app.setColor('#ff0000');
  await app.setSize(16);
  await app.drag(20, 300, 60, 300);
  expect(isNear(await app.pixel(30, 300), 255, 0, 0), 'штрих не лёг').toBe(true);

  const before = (await app.history()).labels.length;

  await dragHandle(app, 'w', -140, 0);

  const after = await app.history();
  expect(after.labels.length - before, 'запись не одна').toBe(1);
  expect(after.labels[after.labels.length - 1]).toContain('Размер');

  await app.undo();
  expect(await size(app), 'отмена не вернула размер').toEqual([900, 600]);
  expect(isNear(await app.pixel(30, 300), 255, 0, 0), 'отмена не вернула рисунок на место').toBe(true);
});

test('растягивание слева сохраняет все слои, а не только бумагу', async ({ page }) => {
  // Смещение обязано примениться к каждому слою одинаково: разъедься они на пиксель -
  // рисунок расслоится, и увидеть это можно будет только глазами.
  const app = await openApp(page);
  await page.evaluate(() => { addLayer(); });
  await page.evaluate(() => {
    const g = state.layers[1].canvas.getContext('2d');
    g.fillStyle = '#0000ff';
    g.fillRect(14, 294, 12, 12);
    composite();
  });

  await dragHandle(app, 'w', -140, 0);

  const [w] = await size(app);
  const grew = w - 900;
  expect(await page.evaluate(() => state.layers.length)).toBe(2);
  expect(isNear(await app.pixel(20 + grew, 300), 0, 0, 255), 'верхний слой не поехал').toBe(true);
});
