// Лента отмен: что в неё попадает, что нет, и как по ней ходят.

const { test, expect } = require('@playwright/test');
const { openApp, isWhite } = require('./harness');

// ─────────── Правка, ничего не изменившая, в ленту не попадает ───────────
// Это то, что в WPF-версии делает IDocumentCommand.ChangedAnything (1.20.0).

test('прозрачный мазок не пишет в ленту', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('brush');
  await app.setOpacity(0);
  const before = await app.fingerprint();
  const n = (await app.history()).labels.length;
  await app.drag(200, 200, 500, 400);
  expect(await app.fingerprint()).toBe(before);
  expect((await app.history()).labels.length).toBe(n);
});

test('фигура нулевого размера не пишет в ленту', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('rect');
  const before = await app.fingerprint();
  const n = (await app.history()).labels.length;
  await app.clickAt(400, 300);
  expect(await app.fingerprint()).toBe(before);
  expect((await app.history()).labels.length).toBe(n);
});

test('заливка тем цветом, что уже под курсором, не пишет в ленту', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('fill');
  await app.setColor('#ffffff');
  const n = (await app.history()).labels.length;
  await app.clickAt(400, 300);
  expect((await app.history()).labels.length).toBe(n);
});

test('штрих мимо холста не пишет в ленту', async ({ page }) => {
  const app = await openApp(page);
  const n = (await app.history()).labels.length;
  await app.drag(-200, -200, -150, -150);
  expect((await app.history()).labels.length).toBe(n);
});

test('пустой жест после отмены не отнимает возможность повтора', async ({ page }) => {
  // Порядок внутри saveHistory: проверка «изменилось ли что-нибудь» стоит ДО обрезки
  // хвоста. Иначе жест, не добавивший ничего, снёс бы повтор - отняв взамен пустоты.
  const app = await openApp(page);
  await app.pickTool('pencil');
  await app.setSize(10);
  await app.drag(100, 100, 200, 200);
  await app.drag(400, 100, 500, 200);
  const two = await app.fingerprint();

  await app.undo();
  await app.pickTool('rect');
  await app.clickAt(700, 500);          // фигура нулевого размера - пустой жест

  await app.redo();
  expect(await app.fingerprint()).toBe(two);
});

// ─────────── Ходьба по ленте ───────────

test('отмена и новый штрих отрезают хвост повтора', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('pencil');
  await app.setSize(8);
  await app.drag(100, 100, 200, 200);
  await app.drag(300, 100, 400, 200);
  expect((await app.history()).labels.length).toBe(3);

  await app.undo();
  await app.drag(500, 100, 600, 200);
  const h = await app.history();
  expect(h.labels.length).toBe(3);
  expect(h.index).toBe(2);
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

test('череда отмен и повторов оставляет холст в кадре по курсору', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('pencil');
  await app.setSize(10);
  await app.drag(100, 100, 200, 200);
  await app.drag(300, 100, 400, 200);
  await app.drag(500, 100, 600, 200);
  const three = await app.fingerprint();

  await app.page.evaluate(() => { undo(); undo(); redo(); undo(); redo(); redo(); });
  await app.settle();
  expect((await app.history()).index).toBe(3);
  expect(await app.fingerprint()).toBe(three);
});

test('двадцать штрихов, десять отмен и десять повторов сходятся', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('pencil');
  await app.setSize(6);
  const snaps = [await app.fingerprint()];
  for (let i = 0; i < 20; i++) {
    await app.drag(20 + i * 40, 100, 40 + i * 40, 500);
    snaps.push(await app.fingerprint());
  }
  for (let i = 0; i < 10; i++) await app.undo();
  expect(await app.fingerprint()).toBe(snaps[10]);
  for (let i = 0; i < 10; i++) await app.redo();
  expect(await app.fingerprint()).toBe(snaps[20]);
});

test('рисование сразу после отмены не оставляет на холсте отменённый кадр', async ({ page }) => {
  // Кадр истории грузится картинкой, то есть асинхронно. Между Ctrl+Z и появлением
  // кадра лежат прежние пиксели - см. state.restoring.
  const app = await openApp(page);
  await app.pickTool('pencil');
  await app.setSize(12);
  await app.drag(100, 100, 300, 300);
  await app.drag(400, 100, 600, 300);

  await app.page.evaluate(() => undo());
  const p = await app.toScreen(700, 500);
  await page.mouse.move(p.x, p.y);
  await page.mouse.down();
  await page.mouse.up();
  await app.settle();

  expect(isWhite(await app.pixel(500, 200))).toBe(true);
  expect(isWhite(await app.pixel(700, 500))).toBe(false);
});

test('переход к записи ленты приводит холст к ней', async ({ page }) => {
  const app = await openApp(page);
  const clean = await app.fingerprint();
  await app.pickTool('pencil');
  await app.setSize(10);
  await app.drag(100, 100, 200, 200);
  const one = await app.fingerprint();
  await app.drag(300, 100, 400, 200);
  const two = await app.fingerprint();

  await app.page.evaluate(() => jumpHistory(0));
  await app.settle();
  expect(await app.fingerprint()).toBe(clean);

  await app.page.evaluate(() => jumpHistory(2));
  await app.settle();
  expect(await app.fingerprint()).toBe(two);

  await app.page.evaluate(() => jumpHistory(1));
  await app.settle();
  expect(await app.fingerprint()).toBe(one);
});

test('переход за пределы ленты ничего не делает', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('pencil');
  await app.drag(100, 100, 200, 200);
  const now = await app.fingerprint();
  const idx = (await app.history()).index;

  await app.page.evaluate(() => { jumpHistory(-1); jumpHistory(99); });
  await app.settle();
  expect((await app.history()).index).toBe(idx);
  expect(await app.fingerprint()).toBe(now);
});

// ─────────── Потолки ───────────

test('лента не растёт бесконечно, а курсор остаётся на последнем кадре', async ({ page }) => {
  test.setTimeout(180000);
  const app = await openApp(page);
  for (let i = 0; i < 160; i++) {
    await app.page.evaluate((k) => {
      const g = lctx();
      g.fillStyle = '#000';
      g.fillRect((k * 7) % 800, Math.floor(k / 100) * 5, 3, 3);
      saveHistory('Штрих');
    }, i);
  }
  const h = await app.history();
  expect(h.labels.length).toBeLessThanOrEqual(150);
  expect(h.index).toBe(h.labels.length - 1);
});

test('самый свежий кадр не выбрасывается, каким бы тяжёлым он ни был', async ({ page }) => {
  const app = await openApp(page);
  await app.page.evaluate(() => {
    const g = lctx();
    g.fillStyle = '#123456';
    g.fillRect(0, 0, canvas.width, canvas.height);
    saveHistory('Тяжёлый кадр');
  });
  const h = await app.history();
  expect(h.index).toBe(h.labels.length - 1);
  expect((await app.pixel(450, 300)).slice(0, 3)).toEqual([0x12, 0x34, 0x56]);
});

// ─────────── Метка «сохранено» ───────────

test('сохранение в отрезанном хвосте не считается сохранением', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('pencil');
  await app.drag(100, 100, 200, 200);
  await app.page.evaluate(() => { state.savedHistoryIndex = state.historyIndex; });
  expect(await app.isDirty()).toBe(false);

  await app.undo();
  await app.drag(300, 300, 400, 400);
  expect(await app.isDirty()).toBe(true);
});
