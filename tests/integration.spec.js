// Стыки: слои, лента, буфер, файл и поднятый объект - вместе, а не по отдельности.
//
// Каждая подсистема по отдельности проверена в своём файле. Находки же в этом проекте
// раз за разом приходят из СТЫКОВ: правило записано у одного участника и не записано у
// соседнего. Здесь собраны связки, где такое расхождение было бы заметно.

const { test, expect } = require('@playwright/test');
const { openApp, isWhite, isNear } = require('./harness');

/** Бумага красная, поверх неё слой с синим прямоугольником. */
async function twoLayers(app) {
  await app.page.evaluate(() => {
    const g = lctx();
    g.fillStyle = '#ff0000';
    g.fillRect(0, 0, 900, 600);
    composite();
    addLayer();
    const g2 = lctx();
    g2.fillStyle = '#0000ff';
    g2.fillRect(150, 150, 600, 350);
    composite();
    saveHistory('Подготовка');
  });
  await app.settle();
}

// ─────────── Потолки: что хранится и что из этого разворачивается ───────────

test('снимок стопки на каждую правку не становится неприлично дорогим', async ({ page }) => {
  test.setTimeout(600000);
  const app = await openApp(page);
  const one = await app.page.evaluate(() => {
    const t = performance.now();
    saveHistory('Проба', { kind: 'fill', x: 1, y: 1, color: '#123456', opacity: 1, layerId: activeLayer().id });
    return performance.now() - t;
  });
  await app.page.evaluate(() => { for (let i = 0; i < 9; i++) addLayer(); });
  await app.settle();
  const many = await app.page.evaluate(() => {
    const t = performance.now();
    const g = lctx();
    g.fillStyle = '#654321';
    g.fillRect(0, 0, 40, 40);
    saveHistory('Проба2', { kind: 'fill', x: 2, y: 2, color: '#654321', opacity: 1, layerId: activeLayer().id });
    return performance.now() - t;
  });
  // Десять слоёв - это десять снимков вместо одного. Линейный рост допустим,
  // взрывной - нет.
  expect(many, JSON.stringify({ one, many })).toBeLessThan(Math.max(60, one * 25));
});

test('вес ленты считает и слои, и потолок держится', async ({ page }) => {
  test.setTimeout(600000);
  const app = await openApp(page);
  await app.page.evaluate(() => { for (let i = 0; i < 4; i++) addLayer(); });
  await app.settle();
  const before = await app.page.evaluate(() => historyBytes());
  await app.page.evaluate(() => {
    for (let k = 0; k < 30; k++) {
      const g = lctx();
      g.fillStyle = '#000';
      g.fillRect((k * 13) % 800, (k * 9) % 500, 5, 5);
      saveHistory('Штрих');
    }
  });
  const after = await app.page.evaluate(() => ({ bytes: historyBytes(), n: state.history.length }));
  expect(after.bytes, 'вес ленты не растёт - значит слои в него не входят').toBeGreaterThan(before);
  expect(after.n).toBeLessThanOrEqual(150);
});

// ─────────── Файл ───────────

test('открытие файла посреди работы со слоями не оставляет мусора', async ({ page }) => {
  const app = await openApp(page);
  await app.page.evaluate(() => {
    addLayer();
    const g = lctx();
    g.fillStyle = '#0000ff';
    g.fillRect(0, 0, 900, 600);
    composite();
    saveHistory('Заливка');
    setLayerOpacity(1, 0.4);
  });
  await app.settle();

  const url = await app.page.evaluate(() => {
    const c = document.createElement('canvas');
    c.width = 240;
    c.height = 160;
    const g = c.getContext('2d');
    g.fillStyle = '#ff0000';
    g.fillRect(0, 0, 240, 160);
    return c.toDataURL();
  });
  page.on('dialog', (d) => d.accept());
  await app.page.evaluate((u) => openImageAsDocument(u, 'x.png'), url);
  await app.page.waitForFunction(() => canvas.width === 240);
  await app.settle();

  expect(await app.page.evaluate(() => ({
    n: state.layers.length,
    opacity: state.layers.map((l) => l.opacity),
    sizes: state.layers.map((l) => [l.canvas.width, l.canvas.height]),
  }))).toEqual({ n: 1, opacity: [1], sizes: [[240, 160]] });
  expect(isNear(await app.pixel(120, 80), 255, 0, 0)).toBe(true);
});

test('открытие файла, пока объект в руках, не оставляет его висеть', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('pencil');
  await app.setColor('#000000');
  await app.setSize(20);
  await app.drag(200, 200, 500, 400);
  await app.pickTool('select');
  await app.drag(180, 180, 520, 420);
  await app.clickAt(350, 300);
  expect(await app.page.evaluate(() => !!state.floating), 'подъём не случился').toBe(true);

  const url = await app.page.evaluate(() => {
    const c = document.createElement('canvas');
    c.width = 300;
    c.height = 200;
    const g = c.getContext('2d');
    g.fillStyle = '#00aa00';
    g.fillRect(0, 0, 300, 200);
    return c.toDataURL();
  });
  page.on('dialog', (d) => d.accept());
  await app.page.evaluate((u) => openImageAsDocument(u, 'x.png'), url);
  await app.page.waitForFunction(() => canvas.width === 300);
  await app.settle();
  expect(await app.page.evaluate(() => !!state.floating), 'объект остался в руках').toBe(false);
  expect(isNear(await app.pixel(150, 100), 0, 170, 0)).toBe(true);
});

// ─────────── Выключатель и то, что идёт после него ───────────

test('обрезка после выключенной правки не ломает ленту', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('pencil');
  await app.setColor('#ff0000');
  await app.setSize(24);
  await app.drag(150, 200, 750, 200);
  await app.drag(150, 400, 750, 400);
  await app.page.evaluate(() => setEntryEnabled(1, false));
  await app.settle();
  const withOff = await app.fingerprint();

  await app.pickTool('crop');
  await app.drag(100, 100, 800, 500);
  await app.page.evaluate(() => applyCrop());
  await app.settle();

  expect(await app.page.evaluate(() =>
    state.history.map((e, i) => (canToggleEntry(i) ? i : -1)).filter((i) => i >= 0))).toEqual([]);
  await app.undo();
  expect(await app.fingerprint(), 'отмена обрезки не вернула вид с выключенной правкой').toBe(withOff);
});

test('выключатель работает и на увеличенном холсте', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('pencil');
  await app.setColor('#ff0000');
  await app.setSize(24);
  await app.drag(150, 200, 750, 200);
  await app.drag(150, 400, 750, 400);
  await app.page.evaluate(() => { state.zoom = 2; applyZoom(); });
  await app.settle();

  await app.page.evaluate(() => setEntryEnabled(1, false));
  await app.settle();
  expect(isWhite(await app.pixel(450, 200)), 'выключение на увеличенном холсте не сработало').toBe(true);
  expect(isNear(await app.pixel(450, 400), 255, 0, 0)).toBe(true);
});

// ─────────── Выделение, буфер и слои ───────────

test('«выделить всё» и Delete на верхнем слое открывают бумагу', async ({ page }) => {
  const app = await openApp(page);
  await app.page.evaluate(() => {
    const g = lctx();
    g.fillStyle = '#ff0000';
    g.fillRect(0, 0, 900, 600);
    composite();
    addLayer();
    const g2 = lctx();
    g2.fillStyle = '#0000ff';
    g2.fillRect(0, 0, 900, 600);
    composite();
    saveHistory('Подготовка');
  });
  await app.settle();
  await app.page.evaluate(() => selectAll());
  await app.settle();
  await page.keyboard.press('Delete');
  await app.settle();
  expect(isNear(await app.pixel(450, 300), 255, 0, 0),
    'под стёртым верхним слоем должна открыться бумага').toBe(true);
});

test('копирование берёт сборку, а вставка ложится в активный слой', async ({ page }) => {
  const app = await openApp(page);
  await twoLayers(app);
  await app.pickTool('select');
  await app.drag(200, 200, 500, 400);
  await app.page.evaluate(() => copySelection());

  await app.page.evaluate(() => setActiveLayer(0));
  await app.page.evaluate(() => pasteFromClipboard());
  await app.settle();
  expect(await app.page.evaluate(() => !!state.floating), 'вставка не случилась').toBe(true);
  await app.drag(150, 150, 700, 480);
  await page.keyboard.press('Enter');
  await app.settle();

  const st = await app.page.evaluate(() => ({
    inPaper: (() => {
      const d = state.layers[0].canvas.getContext('2d').getImageData(700, 480, 1, 1).data;
      return d[2] > 150 && d[0] < 100;
    })(),
    n: state.layers.length,
  }));
  expect(st.n).toBe(2);
  expect(st.inPaper, 'вставка ушла не в активный слой').toBe(true);
});

// ─────────── Полигон, текст и поднятое на верхнем слое ───────────

test('полигон, поднятый с верхнего слоя, туда же и прижимается', async ({ page }) => {
  const app = await openApp(page);
  await twoLayers(app);
  await app.pickTool('quad');
  await app.drag(200, 200, 600, 450);
  expect(await app.page.evaluate(() => !!state.floating), 'полигон не поднялся').toBe(true);
  await app.drag(400, 320, 500, 380);
  await page.keyboard.press('Enter');
  await app.settle();

  const inPaper = await app.page.evaluate(() => {
    const d = state.layers[0].canvas.getContext('2d').getImageData(500, 380, 1, 1).data;
    return d[2] > 180 && d[0] < 80;
  });
  expect(inPaper, 'полигон прижался в бумагу').toBe(false);
});

test('отмена полигона на верхнем слое возвращает картинку', async ({ page }) => {
  const app = await openApp(page);
  await twoLayers(app);
  const before = await app.fingerprint();

  await app.pickTool('quad');
  await app.drag(200, 200, 600, 450);
  expect(await app.page.evaluate(() => !!state.floating), 'полигон не поднялся').toBe(true);
  await app.drag(400, 320, 550, 400);
  await page.keyboard.press('Enter');
  await app.settle();
  expect(await app.fingerprint()).not.toBe(before);

  await app.undo();
  expect(await app.fingerprint(), 'отмена не вернула картинку').toBe(before);
});

test('текст на верхнем слое не попадает в бумагу', async ({ page }) => {
  const app = await openApp(page);
  await twoLayers(app);
  const paperBefore = await app.page.evaluate(() => state.layers[0].canvas.toDataURL());

  await app.pickTool('text');
  await app.setColor('#ffffff');
  await app.clickAt(250, 250);
  await app.page.evaluate(() => { textEditor.innerText = 'Надпись'; commitText(); });
  await app.settle();

  expect(await app.page.evaluate(() => state.layers[0].canvas.toDataURL()),
    'текст лёг в бумагу').toBe(paperBefore);
});

test('поворот поднятого на верхнем слое отменяется', async ({ page }) => {
  const app = await openApp(page);
  await twoLayers(app);
  const before = await app.fingerprint();

  await app.pickTool('select');
  await app.drag(180, 180, 620, 470);
  await app.clickAt(400, 320);
  expect(await app.page.evaluate(() => !!state.floating), 'подъём не случился').toBe(true);
  await app.page.evaluate(() => { state.floating.rotation = Math.PI / 6; renderFloating(); });
  await app.drag(400, 320, 450, 350);
  await page.keyboard.press('Enter');
  await app.settle();
  expect(await app.fingerprint()).not.toBe(before);

  await app.undo();
  expect(await app.fingerprint(), 'отмена поворота не вернула картинку').toBe(before);
});

test('обрезка с поднятым объектом на верхнем слое не теряет его молча', async ({ page }) => {
  const app = await openApp(page);
  await twoLayers(app);
  await app.pickTool('select');
  await app.drag(180, 180, 620, 470);
  await app.clickAt(400, 320);
  expect(await app.page.evaluate(() => !!state.floating), 'подъём не случился').toBe(true);
  await app.drag(400, 320, 430, 340);
  const idx = (await app.history()).index;

  await app.pickTool('crop');
  await app.drag(100, 100, 800, 550);
  await app.page.evaluate(() => applyCrop());
  await app.settle();
  const st = await app.page.evaluate(() => ({ floating: !!state.floating, idx: state.historyIndex }));
  expect(st.floating, 'объект остался висеть над обрезанным холстом').toBe(false);
  expect(st.idx, JSON.stringify({ idx, st })).toBeGreaterThan(idx);
});

// ─────────── Инструменты на слоях ───────────

test('маркер на слое накапливается, а не перекрывает сам себя', async ({ page }) => {
  const app = await openApp(page);
  await app.page.evaluate(() => addLayer());
  await app.settle();
  await app.pickTool('marker');
  await app.setColor('#000000');
  await app.setSize(24);
  await app.setOpacity(0.4);
  await app.drag(200, 200, 700, 200);
  const one = (await app.pixel(450, 200))[0];
  await app.drag(200, 400, 700, 400);
  await app.drag(200, 400, 700, 400);
  const two = (await app.pixel(450, 400))[0];
  expect(two, JSON.stringify({ one, two })).toBeLessThan(one);
});

test('заливка на верхнем слое видит границу, нарисованную на бумаге', async ({ page }) => {
  // Заливка читает СБОРКУ: пользователь целится в то, что видит, а рамка может быть
  // нарисована этажом ниже.
  const app = await openApp(page);
  await app.pickTool('rect');
  await app.setColor('#000000');
  await app.setSize(8);
  await app.drag(200, 150, 700, 450);

  await app.page.evaluate(() => addLayer());
  await app.settle();
  await app.pickTool('fill');
  await app.setColor('#00aa00');
  await app.clickAt(450, 300);
  await app.settle();

  expect(isNear(await app.pixel(450, 300), 0, 170, 0)).toBe(true);
  expect(isWhite(await app.pixel(100, 100)), 'заливка вылезла за рамку с нижнего слоя').toBe(true);
});

test('пипетка на полупрозрачном слое даёт то, что видно', async ({ page }) => {
  const app = await openApp(page);
  await app.page.evaluate(() => {
    const g = lctx();
    g.fillStyle = '#ffffff';
    g.fillRect(0, 0, 900, 600);
    composite();
    addLayer();
    const g2 = lctx();
    g2.fillStyle = '#000000';
    g2.fillRect(0, 0, 900, 600);
    composite();
    setLayerOpacity(1, 0.5);
  });
  await app.settle();
  const seen = await app.pixel(450, 300);

  await app.pickTool('picker');
  await app.clickAt(450, 300);
  const picked = await app.page.evaluate(() => state.color);
  const r = parseInt(picked.slice(1, 3), 16);
  expect(Math.abs(r - seen[0]) <= 2, JSON.stringify({ picked, seen })).toBe(true);
});

// ─────────── Документ и сеанс ───────────

test('спрятанный слой считается частью работы при закрытии', async ({ page }) => {
  const app = await openApp(page);
  await app.page.evaluate(() => {
    addLayer();
    const g = lctx();
    g.fillStyle = '#0000ff';
    g.fillRect(0, 0, 900, 600);
    composite();
    saveHistory('Заливка');
    state.savedHistoryIndex = state.historyIndex;
    state.savedDisabledKey = disabledKey();
  });
  await app.settle();
  expect(await app.isDirty()).toBe(false);

  await app.page.evaluate(() => setLayerVisible(1, false));
  await app.settle();
  expect(await app.isDirty(), 'спрятали слой, а документ считается прежним').toBe(true);
});

test('смена размера после правок на двух слоях отменяется до пикселя', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('pencil');
  await app.setColor('#000000');
  await app.setSize(20);
  await app.drag(150, 200, 700, 200);
  await app.page.evaluate(() => addLayer());
  await app.settle();
  await app.drag(150, 400, 700, 400);
  const before = await app.fingerprint();

  await app.page.evaluate(() => {
    document.getElementById('width-input').value = '600';
    document.getElementById('height-input').value = '400';
    resizeCanvas();
  });
  await app.settle();
  await app.undo();
  expect(await app.page.evaluate(() => [canvas.width, canvas.height])).toEqual([900, 600]);
  expect(await app.fingerprint(), 'отмена смены размера не вернула картинку').toBe(before);
  expect(await app.page.evaluate(() => state.layers.length)).toBe(2);
});
