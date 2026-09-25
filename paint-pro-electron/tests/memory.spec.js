// Расход памяти: мерить, а не прикидывать по коду.
//
// Померить аллокатор здесь нечем - холсты слоёв лежат вне кучи JavaScript, и
// performance.memory на них показывает почти ноль. Та же ловушка была в WPF-версии с
// Process.PrivateMemorySize64: поворот трёх слоёв по 34 МБ давал «прирост 0 МБ».
//
// Поэтому считаем не то, что выдал аллокатор, а то, что попросили МЫ САМИ: создание
// холста, getImageData и toDataURL (см. App.measure). Единица та же, что в C#-версии:
// «эта операция просит x2.25 от холста».
//
// У каждой операции записан свой потолок. Потолок - не украшение: он ловит правку,
// которая заведёт лишний холст размером с документ, ещё до того, как это заметят на
// большой фотографии.

const { test, expect } = require('@playwright/test');
const { openApp } = require('./harness');

/** Нарисовать что-нибудь, чтобы мерить не по пустому листу. */
async function prepare(app) {
  await app.pickTool('pencil');
  await app.setColor('#000000');
  // 15, а не 30: с 1.18.0 толщина карандаша - сам размер, а не половина; картинка та же,
  // на которой мерились потолки ниже.
  await app.setSize(15);
  await app.drag(100, 100, 800, 500);
  await app.pickTool('rect');
  await app.setColor('#000000');
  await app.setSize(6);
  await app.drag(600, 380, 850, 560);
}

test('заливка внутри рамки просит немногим больше одного холста', async ({ page }) => {
  // Один холст неизбежен: заливка читает сборку, чтобы понять, где границы. А вот
  // заплатка заводится по габариту закрашенного, а не во весь документ.
  const app = await openApp(page);
  await prepare(app);
  const m = await app.measure(async () => {
    await app.pickTool('fill');
    await app.setColor('#3366cc');
    await app.clickAt(720, 470);
  });
  expect(m.total, JSON.stringify(m)).toBeLessThan(1.5);
  expect(m.canvas, 'заплатка просится во весь холст: ' + JSON.stringify(m)).toBeLessThan(0.4);
});

test('заливка во весь холст не просит больше двух с небольшим', async ({ page }) => {
  const app = await openApp(page);
  const m = await app.measure(async () => {
    await app.pickTool('fill');
    await app.setColor('#3366cc');
    await app.clickAt(450, 300);
  });
  expect(m.total, JSON.stringify(m)).toBeLessThan(2.3);
});

test('подъём небольшого выделения не просит холст целиком', async ({ page }) => {
  // Снимок «до» снимается по габариту выделения: меняется только эта область.
  const app = await openApp(page);
  await prepare(app);
  const m = await app.measure(async () => {
    await app.pickTool('select');
    await app.drag(610, 390, 840, 550);
    await app.clickAt(720, 470);
  });
  expect(m.total, JSON.stringify(m)).toBeLessThan(0.6);
});

test('подъём во весь холст остаётся в разумных пределах', async ({ page }) => {
  // Даже в худшем случае это два прочтения области (пикап и выкусывание белого) плюс
  // снимок «до». Третье прочтение было лишним: под признак «рамка готова» снимался
  // целый ImageData, который никто не читал.
  const app = await openApp(page);
  await prepare(app);
  const m = await app.measure(async () => {
    await app.pickTool('select');
    await app.drag(20, 20, 880, 580);
    await app.clickAt(450, 300);
  });
  expect(m.total, JSON.stringify(m)).toBeLessThan(3);
});

test('«Выделить всё» не копирует холст ради одного признака', async ({ page }) => {
  const app = await openApp(page);
  await prepare(app);
  const m = await app.measure(async () => {
    await app.page.evaluate(() => selectAll());
  });
  expect(m.total, JSON.stringify(m)).toBeLessThan(0.1);
});

test('штрих не просит ничего, кроме своей записи в ленте', async ({ page }) => {
  const app = await openApp(page);
  const m = await app.measure(async () => {
    await app.pickTool('pencil');
    await app.setColor('#ff0000');
    await app.setSize(20);
    await app.drag(150, 550, 750, 560);
  });
  expect(m.canvas, 'штрих завёл холст: ' + JSON.stringify(m)).toBe(0);
  expect(m.total, JSON.stringify(m)).toBeLessThan(0.5);
});

test('поворот и отражение просят ровно один холст', async ({ page }) => {
  const app = await openApp(page);
  await prepare(app);
  for (const op of ['rotateCanvas()', 'flipH()']) {
    const m = await app.measure(async () => {
      await app.page.evaluate((code) => eval(code), op);
    });
    expect(m.canvas, op + ': ' + JSON.stringify(m)).toBeLessThan(1.2);
  }
});

// ─────────── Главное про слои: отмена не должна просить по холсту на слой ───────────

test('отмена при пяти слоях не заводит новых холстов', async ({ page }) => {
  // Пересоздание стопки просило по холсту на КАЖДЫЙ слой, и на время подмены в памяти
  // жили обе стопки сразу - двойной пик на каждой отмене. Холсты переиспользуются.
  const app = await openApp(page);
  await app.page.evaluate(() => { for (let i = 0; i < 4; i++) addLayer(); });
  await app.settle();
  await app.pickTool('pencil');
  await app.setColor('#0000ff');
  await app.setSize(20);
  await app.drag(150, 200, 750, 260);

  const m = await app.measure(async () => { await app.undo(); });
  expect(m.canvas, 'отмена завела холсты: ' + JSON.stringify(m)).toBe(0);
});

test('пересборка ленты при пяти слоях просит около одного холста', async ({ page }) => {
  const app = await openApp(page);
  await app.page.evaluate(() => { for (let i = 0; i < 4; i++) addLayer(); });
  await app.settle();
  await app.pickTool('pencil');
  await app.setColor('#0000ff');
  await app.setSize(20);
  await app.drag(150, 200, 750, 260);

  const m = await app.measure(async () => {
    await app.page.evaluate(() => rebuildTimeline());
  });
  expect(m.canvas, 'пересборка просит по холсту на слой: ' + JSON.stringify(m)).toBeLessThan(2);
});

test('снимок ленты растёт по слоям линейно, а не быстрее', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('pencil');
  await app.setColor('#000000');
  await app.setSize(30);
  await app.drag(100, 100, 800, 500);
  const one = await app.measure(async () => {
    await app.drag(120, 520, 780, 540);
  });

  await app.page.evaluate(() => { for (let i = 0; i < 4; i++) addLayer(); });
  await app.settle();
  const five = await app.measure(async () => {
    await app.drag(120, 560, 780, 570);
  });
  // Пять слоёв - пять снимков вместо одного. Пустые слои жмутся почти в ничто, так что
  // строгой пятикратности быть не должно; ловим взрывной рост.
  expect(five.dataUrl, JSON.stringify({ one, five })).toBeLessThan(Math.max(0.5, one.dataUrl * 8));
});

// ─────────── Потолок стопки ───────────

test('потолок стопки не даёт памяти уйти в разнос', async ({ page }) => {
  // Каждый слой - целый холст. Потолок считается по ПЛОЩАДИ: сто слоёв на маленьком
  // документе безобидны, на большом смертельны.
  const app = await openApp(page);
  const worst = await app.page.evaluate(() => {
    const limit = maxLayersFor(4000, 3000);
    return limit * 4000 * 3000 * 4;
  });
  // Больше двух гигабайт на стопку слоёв не должно просить ни при каком размере.
  expect(worst / (1024 * 1024 * 1024), 'потолок стопки: ' + (worst / 1e9).toFixed(2) + ' ГБ')
    .toBeLessThan(2);
});
