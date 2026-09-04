// Колёсико мыши над холстом меняет размер того, что сейчас в руке.
//
// Место занятое: с Ctrl колесо уже меняло масштаб, а без Ctrl область холста
// прокручивалась - и обязана прокручиваться дальше, иначе увеличенный холст
// окажется заперт. Отсюда правило: размер меняется, только пока курсор над САМИМ
// холстом, и только у инструмента, у которого размер есть.
//
// Проверяется поведение, а не имена функций: колесо крутится настоящее.

const { test, expect } = require('@playwright/test');
const { openApp } = require('./harness');

/** Подвести мышь к точке документа и крутнуть колесо. */
async function wheelAt(app, x, y, ticks, modifier) {
  const p = await app.toScreen(x, y);
  await app.page.mouse.move(p.x, p.y);
  if (modifier) await app.page.keyboard.down(modifier);
  await app.page.mouse.wheel(0, ticks * 120);
  if (modifier) await app.page.keyboard.up(modifier);
  await app.page.evaluate(() => new Promise(requestAnimationFrame));
}

const size = (app) => app.page.evaluate(() => state.size);

test('колесо вверх над холстом увеличивает размер карандаша', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('pencil');
  await app.setSize(10);
  await wheelAt(app, 400, 300, -1);
  expect(await size(app)).toBeGreaterThan(10);
});

test('колесо вниз уменьшает размер', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('pencil');
  await app.setSize(30);
  await wheelAt(app, 400, 300, 1);
  expect(await size(app)).toBeLessThan(30);
});

test('размер меняется у того инструмента, что в руке, и память раздельная', async ({ page }) => {
  // Ластик и кисть помнят свои размеры по отдельности. Колесо обязано попасть
  // в память ТЕКУЩЕГО инструмента, иначе размер «перетечёт» при переключении.
  const app = await openApp(page);
  await app.pickTool('pencil');
  await app.setSize(20);

  await app.pickTool('eraser');
  await page.evaluate(() => setToolSize(40));
  await wheelAt(app, 400, 300, -1);
  const eraser = await page.evaluate(() => state.eraserSize);
  expect(eraser).toBeGreaterThan(40);

  await app.pickTool('pencil');
  expect(await size(app), 'размер кисти утёк вслед за ластиком').toBe(20);
  expect(await page.evaluate(() => state.eraserSize)).toBe(eraser);
});

test('шаг растёт вместе с размером', async ({ page }) => {
  // Один пиксель за засечку означал бы 99 засечек на весь ползунок.
  const app = await openApp(page);
  await app.pickTool('pencil');

  await page.evaluate(() => setToolSize(4));
  await wheelAt(app, 400, 300, -1);
  const small = (await size(app)) - 4;

  await page.evaluate(() => setToolSize(60));
  await wheelAt(app, 400, 300, -1);
  const big = (await size(app)) - 60;

  expect(small).toBe(1);
  expect(big).toBeGreaterThan(small);
});

test('Ctrl с колесом по-прежнему меняет масштаб, а не размер', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('pencil');
  await app.setSize(10);
  const zoomBefore = await page.evaluate(() => state.zoom);

  await wheelAt(app, 400, 300, -1, 'Control');

  expect(await page.evaluate(() => state.zoom), 'масштаб не изменился').toBeGreaterThan(zoomBefore);
  expect(await size(app), 'Ctrl+колесо задело размер').toBe(10);
});

test('над полем вокруг холста колесо размер не трогает', async ({ page }) => {
  // Там .canvas-area прокручивается. Отнять прокрутку значило бы запереть
  // увеличенный холст без возможности доехать до его нижнего края.
  const app = await openApp(page);
  await app.pickTool('pencil');
  await app.setSize(10);

  const box = await app.canvasBox();
  await page.mouse.move(box.x + box.width / 2, box.y - 12);   // над верхним краем
  await page.mouse.wheel(0, -120);
  await page.evaluate(() => new Promise(requestAnimationFrame));

  expect(await size(app)).toBe(10);
});

test('у инструмента без размера колесо ничего не меняет', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('pencil');
  await app.setSize(10);
  await app.pickTool('fill');

  await wheelAt(app, 400, 300, -1);
  expect(await size(app)).toBe(10);
});

test('на пределе колесо говорит словами, а не молчит', async ({ page }) => {
  // Молчаливый отказ читается как поломка приложения.
  const app = await openApp(page);
  await app.pickTool('pencil');

  await page.evaluate(() => setToolSize(1000));           // упрётся в потолок
  const top = await size(app);
  await wheelAt(app, 400, 300, -1);
  expect(await size(app)).toBe(top);
  expect(await app.hintText()).toContain('предел');

  await page.evaluate(() => setToolSize(0));              // упрётся в пол
  const bottom = await size(app);
  await wheelAt(app, 400, 300, 1);
  expect(await size(app)).toBe(bottom);
  expect(await app.hintText()).toContain('предел');
});

test('ползунок и подписи следуют за колесом', async ({ page }) => {
  // Размер живёт в пяти местах сразу. Забытое звено выглядит как «размер
  // сменился, а кружок курсора прежний» - ровно то, чего быть не должно.
  const app = await openApp(page);
  await app.pickTool('brush');
  await page.evaluate(() => setToolSize(12));
  await wheelAt(app, 400, 300, -1);

  const now = await size(app);
  expect(await page.inputValue('#size-input')).toBe(String(now));
  expect(await page.textContent('#size-display')).toBe(String(now));
  expect(await page.textContent('#info-size')).toBe(now + ' px');
});

test('колесо не пишет в ленту истории', async ({ page }) => {
  // Смена размера - это настройка, а не правка рисунка. Запись в ленту сделала
  // бы отмену бесполезной: каждый поворот колеса съедал бы шаг назад.
  const app = await openApp(page);
  await app.pickTool('pencil');
  const before = (await app.history()).labels.length;

  await wheelAt(app, 400, 300, -1);
  await wheelAt(app, 400, 300, -1);
  await wheelAt(app, 400, 300, 1);

  expect((await app.history()).labels.length).toBe(before);
  expect(await app.isDirty(), 'колесо пометило рисунок изменённым').toBe(false);
});

test('колесо и ползунок дают одно и то же', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('pencil');
  await page.evaluate(() => setToolSize(25));
  await wheelAt(app, 400, 300, -1);
  const byWheel = await page.evaluate(() => ({
    size: state.size, brush: state.brushSize, input: sizeInput.value,
  }));

  await page.evaluate(() => setToolSize(25));
  await page.evaluate((v) => {
    sizeInput.value = String(v);
    sizeInput.dispatchEvent(new Event('input'));
  }, byWheel.size);
  const bySlider = await page.evaluate(() => ({
    size: state.size, brush: state.brushSize, input: sizeInput.value,
  }));

  expect(bySlider).toEqual(byWheel);
});
