// Плавающий объект можно утащить за край холста и за край окна (1.19.0).
//
// До 1.19.0 объект вёл только сам холст: как только указатель уходил с холста на панель или
// за окно, событий у холста больше не было, и объект замирал на месте ухода - «упирался»
// посреди пути, хотя кнопка была зажата. Теперь за краем холста его ведёт документ, и объект
// идёт за указателем куда угодно. Что оказалось за краем холста, в картинку просто не попадёт:
// ни подсказок, ни возврата на холст.

const { test, expect } = require('@playwright/test');
const { openApp } = require('./harness');

/** Красный квадрат 200x150 в (300, 200), выделен и поднят в плавающий объект. */
async function floatingSquare(app) {
  await app.pickTool('rect');
  await app.setColor('#ff0000');
  await app.page.evaluate(() => { state.fillMode = 'solid'; });
  await app.drag(300, 200, 500, 350);
  await app.pickTool('select');
  await app.drag(280, 180, 520, 370);
  // Первое касание поднимает выделение в плавающий объект, не сдвигая его.
  await app.drag(400, 280, 400, 280, 1);
  const f = await floating(app);
  expect(f).not.toBeNull();
  return f;
}

async function floating(app) {
  return app.page.evaluate(() => state.floating && {
    x: state.floating.x, y: state.floating.y, dragging: state.draggingFloating,
  });
}

/** Сколько пикселей документа в одной экранной точке. */
async function docPerScreen(app) {
  const b = await app.canvasBox();
  return { kx: b.docW / b.width, ky: b.docH / b.height };
}

/**
 * Взять объект в точке документа и вести указатель по экранным точкам. После каждого
 * отрезка - где объект: так видно, не замер ли он где-то по дороге.
 */
async function dragThrough(app, from, screenPoints, { release = true, steps = 12 } = {}) {
  const start = await app.toScreen(from.x, from.y);
  await app.page.mouse.move(start.x, start.y);
  await app.page.mouse.down();
  const trail = [];
  for (const p of screenPoints) {
    await app.page.mouse.move(p.x, p.y, { steps });
    trail.push({ at: p, f: await floating(app) });
  }
  if (release) await app.page.mouse.up();
  await app.settle();
  return { start, trail };
}

for (const [name, dx, dy] of [
  ['вправо за окно', 1500, 0],
  ['влево за окно', -1500, 0],
  ['вниз за окно', 0, 1200],
  ['вверх за окно', 0, -900],
  ['по диагонали за угол окна', 1400, 1100],
]) {
  test(`объект идёт за указателем ${name}`, async ({ page }) => {
    const app = await openApp(page);
    const f0 = await floatingSquare(app);
    const { kx, ky } = await docPerScreen(app);
    const s = await app.toScreen(400, 280);
    const end = { x: s.x + dx, y: s.y + dy };
    await dragThrough(app, { x: 400, y: 280 }, [end]);
    const f1 = await floating(app);
    expect(f1.x).toBeCloseTo(f0.x + dx * kx, 0);
    expect(f1.y).toBeCloseTo(f0.y + dy * ky, 0);
    expect(f1.dragging).toBe(false);
  });
}

test('по дороге объект нигде не замирает: каждый шаг - ровно за указателем', async ({ page }) => {
  const app = await openApp(page);
  const f0 = await floatingSquare(app);
  const { kx, ky } = await docPerScreen(app);
  const s = await app.toScreen(400, 280);
  // Через панель справа, за окно, вниз, через панель слева и обратно на холст.
  const route = [
    { x: s.x + 700, y: s.y }, { x: s.x + 1400, y: s.y }, { x: s.x + 1400, y: s.y + 900 },
    { x: s.x - 600, y: s.y + 900 }, { x: s.x - 600, y: s.y - 500 }, { x: s.x + 50, y: s.y + 40 },
  ];
  const { trail } = await dragThrough(app, { x: 400, y: 280 }, route);
  for (const { at, f } of trail) {
    expect(f.x).toBeCloseTo(f0.x + (at.x - s.x) * kx, 0);
    expect(f.y).toBeCloseTo(f0.y + (at.y - s.y) * ky, 0);
    expect(f.dragging).toBe(true);
  }
});

test('вытащили за холст и вернули - объект ложится ровно туда, куда привели', async ({ page }) => {
  const app = await openApp(page);
  const f0 = await floatingSquare(app);
  const s = await app.toScreen(400, 280);
  const back = await app.toScreen(450, 300);
  await dragThrough(app, { x: 400, y: 280 }, [{ x: s.x + 1600, y: s.y + 300 }, back]);
  const f1 = await floating(app);
  expect(f1.x).toBeCloseTo(f0.x + 50, 0);
  expect(f1.y).toBeCloseTo(f0.y + 20, 0);
});

test('брошенный за окном объект молча уходит из картинки: без подсказки, одна запись истории', async ({ page }) => {
  const app = await openApp(page);
  await floatingSquare(app);
  const before = (await app.history()).labels.length;
  const s = await app.toScreen(400, 280);
  await dragThrough(app, { x: 400, y: 280 }, [{ x: s.x + 2000, y: s.y + 1500 }]);
  // Бросили - объект так и лежит там, где отпустили, холст его не притянул обратно.
  const f = await floating(app);
  expect(f.x).toBeGreaterThan(900);
  await page.evaluate(() => commitFloating());
  await app.settle();
  expect(await page.evaluate(() => document.getElementById('hint').classList.contains('visible'))).toBe(false);
  // На месте квадрата пусто (подняли и унесли), и нигде на холсте красного нет.
  const red = await page.evaluate(() => {
    const d = document.getElementById('canvas').getContext('2d').getImageData(0, 0, canvas.width, canvas.height).data;
    let n = 0;
    for (let i = 0; i < d.length; i += 4) if (d[i] > 200 && d[i + 1] < 60 && d[i + 2] < 60 && d[i + 3] > 0) n++;
    return n;
  });
  expect(red).toBe(0);
  const h = await app.history();
  expect(h.labels.length).toBe(before + 1);
  expect(h.labels[h.labels.length - 1]).toBe('Перемещение');
  expect(await page.evaluate(() => state.floating)).toBeNull();
});

test('наполовину за краем - на холсте остаётся ровно видимая половина', async ({ page }) => {
  const app = await openApp(page);
  const f0 = await floatingSquare(app);
  const { kx } = await docPerScreen(app);
  const b = await app.canvasBox();
  // Сдвиг так, чтобы левый край квадрата встал на 800: справа 100 на холсте, 100 за краем.
  const dxDoc = 800 - (f0.x + 20);
  const s = await app.toScreen(400, 280);
  await dragThrough(app, { x: 400, y: 280 }, [{ x: s.x + dxDoc / kx + 900, y: s.y }, { x: s.x + dxDoc / kx, y: s.y }]);
  await page.evaluate(() => commitFloating());
  await app.settle();
  expect(b.docW).toBe(900);
  // Справа от 800 - красный, левее - прежний белый фон.
  const [r, g] = await app.pixel(850, 280);
  expect(r).toBeGreaterThan(200);
  expect(g).toBeLessThan(60);
  const [, g2] = await app.pixel(790, 280);
  expect(g2).toBeGreaterThan(200);
});

test('отпустили кнопку за окном - перетаскивание закончено, следующее движение объект не тащит', async ({ page }) => {
  const app = await openApp(page);
  await floatingSquare(app);
  const s = await app.toScreen(400, 280);
  await dragThrough(app, { x: 400, y: 280 }, [{ x: s.x + 1800, y: s.y }]);
  const f1 = await floating(app);
  await page.mouse.move(s.x, s.y, { steps: 5 });
  const f2 = await floating(app);
  expect(f2.x).toBe(f1.x);
  expect(f2.y).toBe(f1.y);
});

test('кнопку отпустили там, где не слышно (mouseup не пришёл) - первое движение без кнопки жест закрывает', async ({ page }) => {
  const app = await openApp(page);
  await floatingSquare(app);
  const s = await app.toScreen(400, 280);
  await dragThrough(app, { x: 400, y: 280 }, [{ x: s.x + 1800, y: s.y }], { release: false });
  expect((await floating(app)).dragging).toBe(true);
  // mouseup потерялся: движение с отпущенной кнопкой.
  await page.evaluate(() => document.dispatchEvent(new MouseEvent('mousemove', { clientX: 10, clientY: 10, buttons: 0, bubbles: true })));
  expect((await floating(app)).dragging).toBe(false);
});

test('проведённый над кнопками панели объект не жмёт их: инструмент прежний', async ({ page }) => {
  const app = await openApp(page);
  await floatingSquare(app);
  const btn = await page.locator('[data-tool="pencil"]').boundingBox();
  const s = await app.toScreen(400, 280);
  await dragThrough(app, { x: 400, y: 280 }, [{ x: btn.x + btn.width / 2, y: btn.y + btn.height / 2 }]);
  expect(await page.evaluate(() => state.tool)).toBe('select');
  const f = await floating(app);
  expect(f).not.toBeNull();
  // и объект действительно доехал туда, где кнопка
  const { kx } = await docPerScreen(app);
  expect(f.x).toBeLessThan(300 - (s.x - (btn.x + btn.width / 2)) * kx + 25);
});

test('четырёхугольник тоже уезжает за край целиком, все четыре угла вместе', async ({ page }) => {
  const app = await openApp(page);
  await floatingSquare(app);
  await page.evaluate(() => {
    const f = state.floating;
    f.quad = [
      { x: f.x, y: f.y }, { x: f.x + f.w, y: f.y },
      { x: f.x + f.w, y: f.y + f.h }, { x: f.x, y: f.y + f.h },
    ];
  });
  const q0 = await page.evaluate(() => state.floating.quad.map((p) => ({ ...p })));
  const { kx, ky } = await docPerScreen(app);
  const s = await app.toScreen(400, 280);
  await dragThrough(app, { x: 400, y: 280 }, [{ x: s.x + 1300, y: s.y + 700 }]);
  const q1 = await page.evaluate(() => state.floating.quad);
  q1.forEach((p, i) => {
    expect(p.x).toBeCloseTo(q0[i].x + 1300 * kx, 0);
    expect(p.y).toBeCloseTo(q0[i].y + 700 * ky, 0);
  });
});
