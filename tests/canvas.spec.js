// Холст целиком: размер, повороты, обрезка, открытие файла, масштаб и панорама.

const { test, expect } = require('@playwright/test');
const { openApp, isWhite, isNear } = require('./harness');

// ─────────── Размер ───────────

test('нулевой и отрицательный размер холста отклоняются', async ({ page }) => {
  const app = await openApp(page);
  expect(await app.page.evaluate(() => [
    canvasSizeAllowed(0, 100), canvasSizeAllowed(100, 0),
    canvasSizeAllowed(-5, 100), canvasSizeAllowed(100, 100),
  ])).toEqual([false, false, false, true]);
});

test('запредельный размер холста отклоняется', async ({ page }) => {
  const app = await openApp(page);
  expect(await app.page.evaluate(() => canvasSizeAllowed(100000, 100000))).toBe(false);
});

test('смену размера можно отменить до пикселя', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('pencil');
  await app.setSize(12);
  await app.drag(100, 100, 300, 300);
  const before = await app.fingerprint();

  await app.page.evaluate(() => {
    document.getElementById('width-input').value = '400';
    document.getElementById('height-input').value = '300';
    resizeCanvas();
  });
  await app.settle();
  await app.undo();

  expect(await app.page.evaluate(() => [canvas.width, canvas.height])).toEqual([900, 600]);
  expect(await app.fingerprint()).toBe(before);
});

test('увеличение холста сохраняет рисунок в левом верхнем углу', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('pencil');
  await app.setColor('#000000');
  await app.setSize(20);
  await app.drag(100, 100, 300, 100);
  const mark = await app.pixel(200, 100);

  await app.page.evaluate(() => {
    document.getElementById('width-input').value = '1200';
    document.getElementById('height-input').value = '800';
    resizeCanvas();
  });
  await app.settle();

  expect(await app.page.evaluate(() => [canvas.width, canvas.height])).toEqual([1200, 800]);
  expect(await app.pixel(200, 100)).toEqual(mark);
  expect(isWhite(await app.pixel(1100, 700))).toBe(true);
});

// ─────────── Повороты и отражения ───────────

test('четыре поворота на 90 возвращают картинку', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('pencil');
  await app.setSize(12);
  await app.drag(150, 120, 400, 260);
  const before = await app.fingerprint();
  for (let i = 0; i < 4; i++) await app.page.evaluate(() => rotateCanvas(90));
  await app.settle();
  expect(await app.fingerprint()).toBe(before);
});

test('поворот на 90 меняет стороны холста местами', async ({ page }) => {
  const app = await openApp(page);
  await app.page.evaluate(() => rotateCanvas(90));
  await app.settle();
  expect(await app.page.evaluate(() => [canvas.width, canvas.height])).toEqual([600, 900]);
});

test('два отражения по горизонтали возвращают картинку', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('pencil');
  await app.setSize(12);
  await app.drag(150, 120, 400, 260);
  const before = await app.fingerprint();
  await app.page.evaluate(() => { flipH(); flipH(); });
  await app.settle();
  expect(await app.fingerprint()).toBe(before);
});

test('два отражения по вертикали возвращают картинку', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('pencil');
  await app.setSize(12);
  await app.drag(150, 120, 400, 260);
  const before = await app.fingerprint();
  await app.page.evaluate(() => { flipV(); flipV(); });
  await app.settle();
  expect(await app.fingerprint()).toBe(before);
});

test('очистку холста можно отменить', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('pencil');
  await app.setSize(12);
  await app.drag(150, 120, 400, 260);
  const before = await app.fingerprint();

  page.on('dialog', (d) => d.accept());
  await app.page.evaluate(() => clearCanvas());
  await app.settle();
  expect(await app.fingerprint()).not.toBe(before);

  await app.undo();
  expect(await app.fingerprint()).toBe(before);
});

// ─────────── Обрезка ───────────

test('обрезку можно отменить, вернув и размер, и пиксели', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('pencil');
  await app.setSize(14);
  await app.drag(150, 150, 700, 450);
  const before = await app.fingerprint();

  await app.pickTool('crop');
  await app.drag(200, 200, 600, 400);
  await app.page.evaluate(() => applyCrop());
  await app.settle();
  expect(await app.page.evaluate(() => canvas.width)).toBeLessThan(900);

  await app.undo();
  expect(await app.page.evaluate(() => [canvas.width, canvas.height])).toEqual([900, 600]);
  expect(await app.fingerprint()).toBe(before);
});

test('обрезка справа налево даёт тот же холст, что и слева направо', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('crop');
  await app.drag(200, 150, 600, 450);
  await app.page.evaluate(() => applyCrop());
  await app.settle();
  const forward = await app.page.evaluate(() => [canvas.width, canvas.height]);

  await app.undo();
  await app.pickTool('crop');
  await app.drag(600, 450, 200, 150);
  await app.page.evaluate(() => applyCrop());
  await app.settle();
  expect(await app.page.evaluate(() => [canvas.width, canvas.height])).toEqual(forward);
});

test('отказ от обрезки не меняет холст', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('pencil');
  await app.setSize(12);
  await app.drag(150, 150, 700, 450);
  const before = await app.fingerprint();
  const n = (await app.history()).labels.length;

  await app.pickTool('crop');
  await app.drag(200, 200, 600, 400);
  await app.page.evaluate(() => cancelCrop());
  await app.settle();

  expect(await app.fingerprint()).toBe(before);
  expect((await app.history()).labels.length).toBe(n);
});

// ─────────── Открытие файла ───────────
// Картинка заменяет документ целиком, а не ложится плавающим объектом поверх рисунка:
// иначе Ctrl+S записывал бы в файл пользователя его же картинку с чужими штрихами снизу.

const makeImage = (w, h, paint) => `(() => {
  const c = document.createElement('canvas');
  c.width = ${w}; c.height = ${h};
  const g = c.getContext('2d');
  ${paint}
  return c.toDataURL();
})()`;

test('открытая картинка заменяет документ, а не ложится поверх', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('pencil');
  await app.setColor('#000000');
  await app.setSize(20);
  await app.drag(100, 100, 800, 500);

  const url = await app.page.evaluate(makeImage(200, 120,
    "g.fillStyle = '#ff0000'; g.fillRect(0, 0, 200, 120);"));
  page.on('dialog', (d) => d.accept());
  await app.page.evaluate((u) => openImageAsDocument(u, 'test.png'), url);
  await app.page.waitForFunction(() => canvas.width === 200);
  await app.settle();

  expect(await app.page.evaluate(() => [canvas.width, canvas.height])).toEqual([200, 120]);
  expect(isNear(await app.pixel(100, 60), 255, 0, 0)).toBe(true);
});

test('только что открытый файл не считается несохранённой работой', async ({ page }) => {
  const app = await openApp(page);
  const url = await app.page.evaluate(makeImage(300, 200,
    "g.fillStyle = '#00aa00'; g.fillRect(0, 0, 300, 200);"));
  page.on('dialog', (d) => d.accept());
  await app.page.evaluate((u) => openImageAsDocument(u, 'test.png'), url);
  await app.page.waitForFunction(() => canvas.width === 300);
  await app.settle();
  expect(await app.isDirty()).toBe(false);
});

test('картинка помещается целиком, её правый нижний угол не уезжает за край', async ({ page }) => {
  const app = await openApp(page);
  const url = await app.page.evaluate(makeImage(1600, 1000,
    "g.fillStyle = '#ffffff'; g.fillRect(0, 0, 1600, 1000);"
    + "g.fillStyle = '#0000ff'; g.fillRect(1560, 960, 40, 40);"));
  page.on('dialog', (d) => d.accept());
  await app.page.evaluate((u) => openImageAsDocument(u, 'big.png'), url);
  await app.page.waitForFunction(() => canvas.width !== 900);
  await app.settle();

  const size = await app.page.evaluate(() => [canvas.width, canvas.height]);
  expect(isNear(await app.pixel(size[0] - 5, size[1] - 5), 0, 0, 255)).toBe(true);
});

// ─────────── Масштаб и панорама ───────────

test('при увеличении штрих ложится туда, куда указала мышь', async ({ page }) => {
  const app = await openApp(page);
  await app.page.evaluate(() => { state.zoom = 2; applyZoom(); });
  await app.pickTool('pencil');
  await app.setColor('#ff0000');
  await app.setSize(16);
  await app.drag(300, 300, 400, 300);
  expect(isNear(await app.pixel(350, 300), 255, 0, 0)).toBe(true);
  expect(isWhite(await app.pixel(350, 380))).toBe(true);
});

test('при уменьшении штрих ложится туда, куда указала мышь', async ({ page }) => {
  const app = await openApp(page);
  await app.page.evaluate(() => { state.zoom = 0.5; applyZoom(); });
  await app.pickTool('pencil');
  await app.setColor('#ff0000');
  await app.setSize(24);
  await app.drag(300, 300, 500, 300);
  expect(isNear(await app.pixel(400, 300), 255, 0, 0)).toBe(true);
});

test('панорама не сбивает координаты рисования', async ({ page }) => {
  const app = await openApp(page);
  await app.page.evaluate(() => { state.panX = 120; state.panY = 80; applyPan(); });
  await app.pickTool('pencil');
  await app.setColor('#ff0000');
  await app.setSize(16);
  await app.drag(300, 300, 400, 300);
  expect(isNear(await app.pixel(350, 300), 255, 0, 0)).toBe(true);
});

test('масштаб держится в разумных границах и не пишет в ленту', async ({ page }) => {
  const app = await openApp(page);
  const n = (await app.history()).index;
  for (let i = 0; i < 30; i++) await app.page.evaluate(() => zoomOut());
  const lo = await app.page.evaluate(() => state.zoom);
  await app.page.evaluate(() => zoomReset());
  expect(await app.page.evaluate(() => state.zoom)).toBe(1);
  for (let i = 0; i < 30; i++) await app.page.evaluate(() => zoomIn());
  const hi = await app.page.evaluate(() => state.zoom);

  expect(lo).toBeGreaterThanOrEqual(0.05);
  expect(hi).toBeLessThanOrEqual(16);
  await app.page.evaluate(() => { resetPan(); zoomReset(); });
  expect((await app.history()).index).toBe(n);
});
