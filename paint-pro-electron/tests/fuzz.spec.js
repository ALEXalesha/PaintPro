// Фаззинг ленты: случайные последовательности правок и инварианты поверх них.
//
// Сценарная проверка ловит то, что придумал автор. Фаззер ловит то, чего он не придумал:
// в C#-версии из десяти находок 1.16.0 две воспроизводились только определённым порядком
// нескольких операций. Здесь тот же приём, и пятый инвариант - про выключатель.

const { test, expect } = require('@playwright/test');
const { openApp } = require('./harness');

/** Случайное по seed'у, без Math.random: отказ должен воспроизводиться. */
function rng(seed) {
  let x = seed >>> 0;
  return () => {
    x ^= x << 13; x >>>= 0;
    x ^= x >> 17;
    x ^= x << 5; x >>>= 0;
    return x / 4294967296;
  };
}

const COLORS = ['#ff0000', '#00aa00', '#0000ff', '#000000', '#ffaa00'];

/**
 * Один случайный шаг. Возвращает true, если правка складывающаяся (выключаемая).
 * Правки, кладущие готовый снимок, тоже нужны: без них не проверить, что они ОТНИМАЮТ
 * галочку у всего, что выше.
 */
async function step(app, rnd, allowSnapshots) {
  const kinds = allowSnapshots
    ? ['stroke', 'shape', 'fill', 'erase', 'text', 'flip', 'layer']
    : ['stroke', 'shape', 'fill', 'erase', 'text'];
  const kind = kinds[Math.floor(rnd() * kinds.length)];
  const x1 = 60 + Math.floor(rnd() * 700);
  const y1 = 60 + Math.floor(rnd() * 450);
  const x2 = 60 + Math.floor(rnd() * 700);
  const y2 = 60 + Math.floor(rnd() * 450);
  const color = COLORS[Math.floor(rnd() * COLORS.length)];

  if (kind === 'stroke') {
    await app.pickTool(rnd() < 0.5 ? 'pencil' : 'brush');
    await app.setColor(color);
    await app.setSize(6 + Math.floor(rnd() * 24));
    await app.setOpacity(0.4 + rnd() * 0.6);
    await app.drag(x1, y1, x2, y2);
    return true;
  }
  if (kind === 'shape') {
    await app.pickTool(['rect', 'ellipse', 'line', 'triangle'][Math.floor(rnd() * 4)]);
    await app.setColor(color);
    await app.setSize(4 + Math.floor(rnd() * 10));
    await app.setOpacity(1);
    await app.page.evaluate((m) => setFillMode(m), rnd() < 0.5 ? 'solid' : 'outline');
    await app.drag(x1, y1, x2, y2);
    return true;
  }
  if (kind === 'fill') {
    await app.pickTool('fill');
    await app.setColor(color);
    await app.setOpacity(1);
    await app.clickAt(x1, y1);
    return true;
  }
  if (kind === 'erase') {
    await app.pickTool('eraser');
    await app.page.evaluate((sz) => { state.eraserSize = sz; syncSizeForTool(); },
      20 + Math.floor(rnd() * 40));
    await app.drag(x1, y1, x2, y2);
    return true;
  }
  if (kind === 'text') {
    await app.pickTool('text');
    await app.setOpacity(1);
    await app.setColor(color);
    await app.clickAt(x1, y1);
    await app.page.evaluate((t) => { textEditor.innerText = t; commitText(); }, 'Текст ' + Math.floor(rnd() * 100));
    await app.settle();
    return true;
  }
  if (kind === 'flip') {
    await app.page.evaluate((v) => (v ? flipH() : flipV()), rnd() < 0.5);
    await app.settle();
    return false;
  }
  await app.page.evaluate(() => addLayer());
  await app.settle();
  return false;
}

/** Номера записей, у которых сейчас есть галочка. */
async function toggleable(app) {
  return app.page.evaluate(() =>
    state.history.map((e, i) => (canToggleEntry(i) ? i : -1)).filter((i) => i >= 0));
}

async function setDisabled(app, indices) {
  await app.page.evaluate(async (list) => {
    for (let i = 0; i < state.history.length; i++) {
      const want = !list.includes(i);
      if (canToggleEntry(i) && (state.history[i].enabled !== false) !== want) {
        await setEntryEnabled(i, want);
      }
    }
  }, indices);
  await app.settle();
}

test('один и тот же набор выключенных даёт один и тот же документ', async ({ page }) => {
  test.setTimeout(600000);
  const app = await openApp(page);
  const rnd = rng(20260901);

  for (let i = 0; i < 8; i++) await step(app, rnd, false);
  const all = await toggleable(app);
  expect(all.length, 'выключаемых записей не набралось').toBeGreaterThan(3);

  const pick = all.filter((_, k) => k % 2 === 0);

  await setDisabled(app, pick);
  const a = await app.fingerprint();

  // Пройти через другое состояние и вернуться к тому же набору.
  await setDisabled(app, all);
  await setDisabled(app, []);
  await setDisabled(app, pick);
  expect(await app.fingerprint(), 'один и тот же набор дал разные документы').toBe(a);
});

test('включить всё обратно - то же, что не выключать вовсе', async ({ page }) => {
  test.setTimeout(600000);
  const app = await openApp(page);
  const rnd = rng(777);

  for (let i = 0; i < 8; i++) await step(app, rnd, false);
  const clean = await app.fingerprint();
  const all = await toggleable(app);

  await setDisabled(app, all);
  expect(await app.fingerprint()).not.toBe(clean);
  await setDisabled(app, []);
  expect(await app.fingerprint(), 'вернули все галочки, а документ другой').toBe(clean);
});

test('выключение одной записи не трогает остальные', async ({ page }) => {
  test.setTimeout(600000);
  const app = await openApp(page);
  const rnd = rng(31337);

  for (let i = 0; i < 6; i++) await step(app, rnd, false);
  const all = await toggleable(app);
  const base = await app.fingerprint();

  for (const i of all) {
    await setDisabled(app, [i]);
    const off = await app.fingerprint();
    await setDisabled(app, []);
    expect(await app.fingerprint(), 'запись ' + i + ': возврат галочки не восстановил документ').toBe(base);
    // Выключение хоть чего-то обязано быть заметно - иначе проверка ничего не проверяет.
    expect(off === base && i > 0, 'запись ' + i + ' выключилась без следа').toBe(false);
  }
});

test('снимок ниже отнимает галочку у всего, что выше', async ({ page }) => {
  test.setTimeout(600000);
  const app = await openApp(page);
  const rnd = rng(4242);

  for (let i = 0; i < 5; i++) await step(app, rnd, false);
  expect((await toggleable(app)).length).toBeGreaterThan(2);

  await app.page.evaluate(() => flipH());
  await app.settle();
  expect(await toggleable(app), 'после снимка не должно остаться ни одной галочки').toEqual([]);
});

test('прогулка по ленте с выключенными записями приводит к одному и тому же', async ({ page }) => {
  test.setTimeout(600000);
  const app = await openApp(page);
  const rnd = rng(90210);

  for (let i = 0; i < 7; i++) await step(app, rnd, false);
  const all = await toggleable(app);
  await setDisabled(app, all.filter((_, k) => k % 3 === 0));

  const here = await app.fingerprint();
  const idx = (await app.history()).index;

  await app.page.evaluate(() => jumpHistory(0));
  await app.settle();
  await app.page.evaluate((i) => jumpHistory(i), idx);
  await app.settle();
  expect(await app.fingerprint(), 'прогулка по ленте изменила документ').toBe(here);
});
