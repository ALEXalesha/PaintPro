// Выключатель правки в ленте. Перенос возможности из C# 1.23.0.
//
// Щелчок по строке истории приводит документ к этой позиции, то есть отменяет всё, что
// сделано после неё. Галочка - другое: снял, и правка перестаёт применяться, а идущие
// после неё остаются на месте.

const { test, expect } = require('@playwright/test');
const { openApp, isWhite, isNear } = require('./harness');

async function stroke(app, color, y) {
  await app.pickTool('pencil');
  await app.setColor(color);
  await app.setSize(24);
  await app.drag(150, y, 750, y);
}

/** Состояние ленты: подписи, галочки и можно ли их трогать. */
async function rows(app) {
  return app.page.evaluate(() => state.history.map((e, i) => ({
    label: e.label,
    enabled: e.enabled !== false,
    canToggle: canToggleEntry(i),
  })));
}

async function toggle(app, i, on) {
  await app.page.evaluate(([k, v]) => setEntryEnabled(k, v), [i, on]);
  await app.settle();
}

// ─────────── Что выключаемо, а что нет ───────────

test('складывающиеся правки выключаемы', async ({ page }) => {
  const app = await openApp(page);
  await stroke(app, '#ff0000', 200);
  await app.pickTool('rect');
  await app.drag(200, 300, 600, 400);
  await app.pickTool('fill');
  await app.setColor('#00aa00');
  await app.clickAt(450, 500);

  const r = await rows(app);
  expect(r.map((x) => x.label)).toEqual(['Исходное состояние', 'Штрих', 'Фигура', 'Заливка']);
  expect(r.slice(1).every((x) => x.canToggle), JSON.stringify(r)).toBe(true);
});

test('правка, кладущая готовый снимок, невыключаема', async ({ page }) => {
  const app = await openApp(page);
  await stroke(app, '#ff0000', 200);
  await app.page.evaluate(() => flipH());
  await app.settle();

  const r = await rows(app);
  expect(r[r.length - 1].label).toMatch(/Отражение/);
  expect(r[r.length - 1].canToggle, 'снимок объявлен выключаемым').toBe(false);
});

test('складывающаяся правка перестаёт быть выключаемой, если ниже лёг снимок', async ({ page }) => {
  const app = await openApp(page);
  await stroke(app, '#ff0000', 200);
  expect((await rows(app))[1].canToggle).toBe(true);

  await app.page.evaluate(() => flipH());
  await app.settle();
  expect((await rows(app))[1].canToggle, 'снимок ниже не отнял галочку').toBe(false);
});

test('отказ в выключении объясняется словами', async ({ page }) => {
  const app = await openApp(page);
  await stroke(app, '#ff0000', 200);
  await app.page.evaluate(() => flipH());
  await app.settle();
  expect(await app.page.evaluate(() => setEntryEnabled(1, false))).toBe(false);
  expect(await app.hintText()).toMatch(/снимок/i);
});

// ─────────── Что делает выключение ───────────

test('выключенная правка перестаёт применяться, а следующие остаются', async ({ page }) => {
  const app = await openApp(page);
  await stroke(app, '#ff0000', 200);
  await stroke(app, '#0000ff', 400);

  expect(isNear(await app.pixel(450, 200), 255, 0, 0)).toBe(true);
  expect(isNear(await app.pixel(450, 400), 0, 0, 255)).toBe(true);

  await toggle(app, 1, false);
  expect(isWhite(await app.pixel(450, 200)), 'выключенный штрих остался на месте').toBe(true);
  expect(isNear(await app.pixel(450, 400), 0, 0, 255), 'вместе с ним пропал и следующий').toBe(true);
});

test('выключить и включить обратно возвращает документ до пикселя', async ({ page }) => {
  const app = await openApp(page);
  await stroke(app, '#ff0000', 200);
  await stroke(app, '#0000ff', 400);
  await app.pickTool('rect');
  await app.setColor('#000000');
  await app.setSize(6);
  await app.drag(200, 100, 700, 500);
  const before = await app.fingerprint();

  await toggle(app, 1, false);
  expect(await app.fingerprint()).not.toBe(before);
  await toggle(app, 1, true);
  expect(await app.fingerprint()).toBe(before);
});

test('порядок выключения не меняет результат', async ({ page }) => {
  const app = await openApp(page);
  await stroke(app, '#ff0000', 150);
  await stroke(app, '#00aa00', 300);
  await stroke(app, '#0000ff', 450);

  await toggle(app, 1, false);
  await toggle(app, 3, false);
  const a = await app.fingerprint();

  await toggle(app, 1, true);
  await toggle(app, 3, true);
  await toggle(app, 3, false);
  await toggle(app, 1, false);
  expect(await app.fingerprint()).toBe(a);
});

test('выключение перекрытой правки открывает то, что было под ней', async ({ page }) => {
  const app = await openApp(page);
  await stroke(app, '#ff0000', 300);
  await stroke(app, '#0000ff', 300);      // тот же след поверх
  expect(isNear(await app.pixel(450, 300), 0, 0, 255)).toBe(true);

  await toggle(app, 2, false);
  expect(isNear(await app.pixel(450, 300), 255, 0, 0), 'под верхним штрихом должен быть красный').toBe(true);
});

test('заливка после выключенного штриха растекается по-новому', async ({ page }) => {
  // Заливку нельзя просто наложить сохранённой заплаткой: подложка изменилась,
  // и она обязана растечься так, как растеклась бы без выключенной правки.
  const app = await openApp(page);
  await app.pickTool('rect');
  await app.setColor('#000000');
  await app.setSize(8);
  await app.drag(200, 150, 700, 450);     // замкнутая рамка

  await app.pickTool('fill');
  await app.setColor('#00aa00');
  await app.clickAt(450, 300);            // заливка внутри рамки
  expect(isWhite(await app.pixel(100, 100)), 'заливка вылезла наружу').toBe(true);

  await toggle(app, 1, false);            // рамки больше нет
  expect(isNear(await app.pixel(100, 100), 0, 170, 0),
    'без рамки заливка обязана растечься на весь холст').toBe(true);
});

// ─────────── Выключенная правка и лента ───────────

test('отмена после выключения возвращает пиксели по обновлённому кадру', async ({ page }) => {
  const app = await openApp(page);
  await stroke(app, '#ff0000', 200);
  await stroke(app, '#0000ff', 400);
  await toggle(app, 1, false);
  const withOff = await app.fingerprint();

  await app.undo();      // ушли на кадр после первого (выключенного) штриха
  expect(isWhite(await app.pixel(450, 200)), 'вернулись пиксели по устаревшему кадру').toBe(true);
  expect(isWhite(await app.pixel(450, 400))).toBe(true);

  await app.redo();
  expect(await app.fingerprint()).toBe(withOff);
});

test('выключенная правка не пропадает из ленты', async ({ page }) => {
  const app = await openApp(page);
  await stroke(app, '#ff0000', 200);
  await stroke(app, '#0000ff', 400);
  const n = (await app.history()).labels.length;
  await toggle(app, 1, false);
  expect((await app.history()).labels.length).toBe(n);
  expect((await rows(app))[1].enabled).toBe(false);
});

// ─────────── Признак несохранённой работы ───────────

test('выключить правку - значит изменить документ, включить обратно - вернуть', async ({ page }) => {
  const app = await openApp(page);
  await stroke(app, '#ff0000', 200);
  await stroke(app, '#0000ff', 400);
  await app.page.evaluate(() => {
    state.savedHistoryIndex = state.historyIndex;
    state.savedDisabledKey = disabledKey();
  });
  expect(await app.isDirty()).toBe(false);

  await toggle(app, 1, false);
  expect(await app.isDirty(), 'выключенная правка не считается изменением').toBe(true);

  await toggle(app, 1, true);
  expect(await app.isDirty(), 'вернули как было, а вопрос при закрытии остался').toBe(false);
});

// ─────────── Панель ───────────

test('в панели истории у складывающейся правки есть галочка, у снимка - нет', async ({ page }) => {
  const app = await openApp(page);
  await stroke(app, '#ff0000', 200);
  await app.page.evaluate(() => flipH());
  await app.settle();

  const boxes = await app.page.evaluate(() =>
    Array.from(document.querySelectorAll('.hist-row')).map((r) => ({
      index: Number(r.dataset.index),
      disabled: r.querySelector('.hist-check').disabled,
    })));
  expect(boxes[boxes.length - 1].disabled, 'у снимка галочка доступна').toBe(true);
  expect(boxes[1].disabled, 'снимок ниже должен был отнять галочку').toBe(true);
});

test('выключенная строка показана зачёркнутой', async ({ page }) => {
  const app = await openApp(page);
  await stroke(app, '#ff0000', 200);
  await toggle(app, 1, false);
  const off = await app.page.evaluate(() =>
    document.querySelector('.hist-row[data-index="1"] .hist-item').classList.contains('off'));
  expect(off).toBe(true);
});

test('щелчок по галочке не перегоняет документ к этой позиции', async ({ page }) => {
  const app = await openApp(page);
  await stroke(app, '#ff0000', 200);
  await stroke(app, '#0000ff', 400);
  const idx = (await app.history()).index;

  await app.page.evaluate(() => {
    document.querySelector('.hist-row[data-index="1"] .hist-check').click();
  });
  await app.settle();
  expect((await app.history()).index, 'галочка увела курсор ленты').toBe(idx);
});

// ─────────── Края и взаимодействия ───────────

// ─── Край: базового снимка в ленте не осталось ───
test('выключение первой записи, когда снимка выше неё нет', async ({ page }) => {
  const app = await openApp(page);
  await stroke(app, '#ff0000', 200);
  await stroke(app, '#0000ff', 400);
  // Выбрасываем «Исходное состояние»: ровно так поступает переполнение ленты.
  await app.page.evaluate(() => {
    state.history.shift();
    state.historyIndex--;
    state.savedHistoryIndex--;
    renderHistoryPanel();
  });
  const can = await app.page.evaluate(() => canToggleEntry(0));
  if (!can) return;                       // отказали - это честно
  const before = await app.fingerprint();
  await toggle(app, 0, false);
  expect(await app.fingerprint(), 'галочку дали, а выключение ничего не сделало').not.toBe(before);
});

// ─── Выключатель и ходьба по ленте ───
test('новая правка после выключения отрезает хвост корректно', async ({ page }) => {
  const app = await openApp(page);
  await stroke(app, '#ff0000', 150);
  await stroke(app, '#00aa00', 300);
  await stroke(app, '#0000ff', 450);
  await toggle(app, 2, false);

  await app.undo();
  await stroke(app, '#000000', 520);
  const h = await app.history();
  expect(h.index).toBe(h.labels.length - 1);
  // Выключенная запись стояла ВЫШЕ курсора, в отрезаемый хвост она не попадала и
  // законно осталась выключенной. А новая запись обязана быть включённой.
  const st = await app.page.evaluate(() => ({
    offs: state.history.map((e, i) => (e.enabled === false ? i : -1)).filter((i) => i >= 0),
    lastEnabled: state.history[state.history.length - 1].enabled !== false,
  }));
  expect(st.offs, JSON.stringify({ h, st })).toEqual([2]);
  expect(st.lastEnabled).toBe(true);

  // И включить старую запись обратно должно быть можно: то, что после неё, тоже
  // складывающееся.
  const before = await app.fingerprint();
  await toggle(app, 2, true);
  expect(await app.fingerprint(), 'вернуть выключенную правку не вышло').not.toBe(before);
});

test('выключение при курсоре не в конце ленты', async ({ page }) => {
  const app = await openApp(page);
  await stroke(app, '#ff0000', 150);
  await stroke(app, '#00aa00', 300);
  await stroke(app, '#0000ff', 450);
  await app.undo();                       // курсор на втором штрихе
  const here = await app.fingerprint();
  const idx = (await app.history()).index;

  await toggle(app, 1, false);
  expect((await app.history()).index, 'курсор уехал').toBe(idx);
  expect(await app.fingerprint()).not.toBe(here);

  await toggle(app, 1, true);
  expect(await app.fingerprint(), 'возврат галочки не восстановил вид').toBe(here);
});

test('повтор после выключения даёт кадр с учётом выключенного', async ({ page }) => {
  const app = await openApp(page);
  await stroke(app, '#ff0000', 150);
  await stroke(app, '#00aa00', 300);
  await stroke(app, '#0000ff', 450);
  await toggle(app, 1, false);
  const end = await app.fingerprint();

  await app.undo();
  await app.undo();
  await app.redo();
  await app.redo();
  expect(await app.fingerprint()).toBe(end);
});

// ─── Выключатель и слои ───
test('выключение штриха на верхнем слое не трогает бумагу', async ({ page }) => {
  const app = await openApp(page);
  await app.page.evaluate(() => {
    const g = lctx();
    g.fillStyle = '#ff0000';
    g.fillRect(0, 0, 900, 600);
    composite();
    saveHistory('Заливка слоя');
    addLayer();
  });
  await app.settle();
  await stroke(app, '#000000', 300);
  expect(isWhite(await app.pixel(450, 300))).toBe(false);

  const can = await app.page.evaluate(() =>
    state.history.map((e, i) => (canToggleEntry(i) ? i : -1)).filter((i) => i >= 0));
  expect(can.length, 'выключаемых записей нет').toBeGreaterThan(0);
  await toggle(app, can[can.length - 1], false);
  expect(isNear(await app.pixel(450, 300), 255, 0, 0), 'под выключенным штрихом не бумага').toBe(true);
});

test('добавление слоя отнимает галочку у того, что выше', async ({ page }) => {
  const app = await openApp(page);
  await stroke(app, '#ff0000', 200);
  expect(await app.page.evaluate(() => canToggleEntry(1))).toBe(true);
  await app.page.evaluate(() => addLayer());
  await app.settle();
  expect(await app.page.evaluate(() => canToggleEntry(1)),
    'добавление слоя кладёт снимок и обязано отнять галочку').toBe(false);
});

// ─── Выключатель и плавающий объект ───
test('прижатие поднятого отнимает галочку у того, что выше', async ({ page }) => {
  const app = await openApp(page);
  await stroke(app, '#ff0000', 300);
  await app.pickTool('select');
  await app.drag(150, 250, 750, 350);
  await app.clickAt(400, 300);
  await app.drag(400, 300, 500, 450);
  await page.keyboard.press('Enter');
  await app.settle();
  expect(await app.page.evaluate(() => canToggleEntry(1)),
    'прижатие кладёт готовый снимок').toBe(false);
});

// ─── Потолок: пересборка длинной ленты ───
test('пересборка ленты в две дюжины записей проходит без расхождений', async ({ page }) => {
  test.setTimeout(600000);
  const app = await openApp(page);
  for (let i = 0; i < 25; i++) {
    await app.page.evaluate((k) => {
      const id = activeLayer().id;
      const x = (k * 31) % 800;
      const y = (k * 17) % 500;
      const g = lctx();
      const saved = { color: state.color, size: state.size, fillMode: state.fillMode };
      state.color = '#000000';
      state.size = 1;
      state.fillMode = 'solid';
      drawShape(g, 'rect', x, y, x + 6, y + 6);
      state.color = saved.color;
      state.size = saved.size;
      state.fillMode = saved.fillMode;
      composite();
      saveHistory('Фигура', {
        kind: 'shape', tool: 'rect', x0: x, y0: y, x1: x + 6, y1: y + 6,
        color: '#000000', size: 1, opacity: 1, fillMode: 'solid', layerId: id
      });
    }, i);
  }
  const before = await app.fingerprint();
  await app.page.evaluate(() => rebuildTimeline());
  await app.settle();
  expect(await app.fingerprint(), 'пересборка без выключений изменила документ').toBe(before);
});

// ─── Сохранение и выключатель ───
test('сохранение запоминает и набор выключенных', async ({ page }) => {
  const app = await openApp(page);
  await stroke(app, '#ff0000', 200);
  await stroke(app, '#0000ff', 400);
  await toggle(app, 1, false);
  await app.page.evaluate(() => {
    state.savedHistoryIndex = state.historyIndex;
    state.savedDisabledKey = disabledKey();
  });
  expect(await app.isDirty()).toBe(false);

  await toggle(app, 1, true);
  expect(await app.isDirty(), 'вернули правку, а документ считается прежним').toBe(true);
});

// ─── Выключенная правка и сохраняемая картинка ───
test('выключенная правка не попадает в сохраняемую картинку', async ({ page }) => {
  const app = await openApp(page);
  await stroke(app, '#ff0000', 200);
  await stroke(app, '#0000ff', 400);
  await toggle(app, 1, false);
  const url = await app.page.evaluate(() => canvas.toDataURL());
  expect(url).toBe(await app.fingerprint());
  expect(isWhite(await app.pixel(450, 200)), 'выключенная правка попала в картинку').toBe(true);
});
