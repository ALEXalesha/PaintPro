// Слои. Двадцать три правки C#-выпусков 1.12.0-1.23.0 касались их, и до 1.12.0
// Electron-версии проверять тут было нечего: слоёв в ней не было вовсе.

const { test, expect } = require('@playwright/test');
const { openApp, isWhite, isNear } = require('./harness');

/** Добавить слой над активным и вернуть его номер. */
async function addLayer(app) {
  return app.page.evaluate(() => { addLayer(); return state.activeLayer; });
}

/** Залить активный слой цветом целиком. */
async function paintLayer(app, hex) {
  await app.page.evaluate((c) => {
    const g = lctx();
    g.fillStyle = c;
    g.fillRect(0, 0, canvas.width, canvas.height);
    composite();
  }, hex);
}

/** Свойства стопки без пикселей. */
async function stack(app) {
  return app.page.evaluate(() => ({
    names: state.layers.map((l) => l.name),
    visible: state.layers.map((l) => l.visible),
    opacity: state.layers.map((l) => l.opacity),
    active: state.activeLayer,
  }));
}

// ─────────── Устройство стопки ───────────

test('у нового документа ровно один слой - бумага', async ({ page }) => {
  const app = await openApp(page);
  const st = await stack(app);
  expect(st.names.length).toBe(1);
  expect(st.active).toBe(0);
  expect(isWhite(await app.pixel(450, 300))).toBe(true);
});

test('добавленный слой встаёт над активным и становится активным', async ({ page }) => {
  const app = await openApp(page);
  expect(await addLayer(app)).toBe(1);
  const st = await stack(app);
  expect(st.names.length).toBe(2);
  expect(st.active).toBe(1);
});

test('слои складываются снизу вверх', async ({ page }) => {
  const app = await openApp(page);
  await paintLayer(app, '#ff0000');
  await addLayer(app);
  await app.page.evaluate(() => {
    const g = lctx();
    g.fillStyle = '#0000ff';
    g.fillRect(0, 0, 400, 300);
    composite();
  });
  expect(isNear(await app.pixel(200, 150), 0, 0, 255), 'верхний слой должен перекрывать').toBe(true);
  expect(isNear(await app.pixel(700, 450), 255, 0, 0), 'там, где верхнего нет, виден нижний').toBe(true);
});

test('имена слоёв не повторяются даже после удаления', async ({ page }) => {
  // Номер, взятый из ЧИСЛА слоёв, повторяется: оно уменьшается при удалении.
  const app = await openApp(page);
  await addLayer(app);
  await addLayer(app);
  await app.page.evaluate(() => removeLayer(1));
  await addLayer(app);
  const names = (await stack(app)).names;
  expect(new Set(names).size, names.join(', ')).toBe(names.length);
});

// ─────────── Видимость и прозрачность ───────────

test('скрытый слой не виден в сборке', async ({ page }) => {
  const app = await openApp(page);
  await addLayer(app);
  await paintLayer(app, '#0000ff');
  expect(isNear(await app.pixel(450, 300), 0, 0, 255)).toBe(true);

  await app.page.evaluate(() => setLayerVisible(1, false));
  expect(isWhite(await app.pixel(450, 300)), 'скрытый слой всё ещё виден').toBe(true);
});

test('слой, выкрученный в полную прозрачность, не подкрашивает картинку', async ({ page }) => {
  const app = await openApp(page);
  await addLayer(app);
  await paintLayer(app, '#0000ff');
  await app.page.evaluate(() => setLayerOpacity(1, 0));
  expect(isWhite(await app.pixel(450, 300)), 'прозрачный слой подкрашивает').toBe(true);
});

test('полупрозрачный слой смешивается с тем, что под ним', async ({ page }) => {
  const app = await openApp(page);
  await paintLayer(app, '#ff0000');
  await addLayer(app);
  await paintLayer(app, '#0000ff');
  await app.page.evaluate(() => setLayerOpacity(1, 0.5));
  const p = await app.pixel(450, 300);
  expect(p[0] > 60 && p[2] > 60, JSON.stringify(p)).toBe(true);
});

// ─────────── Рисование по слоям ───────────

test('краска ложится в активный слой, а не в соседний', async ({ page }) => {
  const app = await openApp(page);
  await addLayer(app);
  await app.pickTool('pencil');
  await app.setColor('#000000');
  await app.setSize(20);
  await app.drag(200, 300, 700, 300);

  const ink = await app.page.evaluate(() => state.layers.map((l) => {
    const d = l.canvas.getContext('2d').getImageData(450, 300, 1, 1).data;
    return d[3] > 0 && d[0] < 100;
  }));
  expect(ink[0], 'краска попала в бумагу').toBe(false);
  expect(ink[1], 'краски нет в активном слое').toBe(true);
});

test('спрятанный слой не съедает штрих молча', async ({ page }) => {
  const app = await openApp(page);
  await addLayer(app);
  await app.page.evaluate(() => setLayerVisible(1, false));
  const n = (await app.history()).labels.length;
  const before = await app.fingerprint();

  await app.pickTool('pencil');
  await app.setSize(20);
  await app.drag(200, 300, 700, 300);

  expect(await app.fingerprint()).toBe(before);
  expect((await app.history()).labels.length).toBe(n);
  expect(await app.hintText()).toMatch(/скрыт/i);
});

test('слой с нулевой прозрачностью не съедает штрих молча', async ({ page }) => {
  const app = await openApp(page);
  await addLayer(app);
  await app.page.evaluate(() => setLayerOpacity(1, 0));
  const n = (await app.history()).labels.length;

  await app.pickTool('pencil');
  await app.setSize(20);
  await app.drag(200, 300, 700, 300);

  expect((await app.history()).labels.length).toBe(n);
  expect(await app.hintText()).toMatch(/прозрач/i);
});

test('ластик на бумаге красит белым, а на слое выше - вычитает', async ({ page }) => {
  const app = await openApp(page);
  // Бумага красная, поверх неё слой с чёрной полосой.
  await paintLayer(app, '#ff0000');
  await addLayer(app);
  await app.pickTool('pencil');
  await app.setColor('#000000');
  await app.setSize(30);
  await app.drag(200, 300, 700, 300);
  expect(isWhite(await app.pixel(450, 300))).toBe(false);

  // Ластик на ВЕРХНЕМ слое: должна открыться красная бумага, а не появиться белое.
  await app.pickTool('eraser');
  await app.page.evaluate(() => { state.eraserSize = 80; syncSizeForTool(); });
  await app.drag(200, 300, 700, 300);
  expect(isNear(await app.pixel(450, 300), 255, 0, 0), 'ластик закрасил белым вместо дыры').toBe(true);

  // А на бумаге он красит белым.
  await app.page.evaluate(() => setActiveLayer(0));
  await app.drag(200, 300, 700, 300);
  expect(isWhite(await app.pixel(450, 300)), 'ластик по бумаге не дал белого').toBe(true);
});

// ─────────── Удаление и порядок ───────────

test('бумагу удалить нельзя, и об этом говорят', async ({ page }) => {
  const app = await openApp(page);
  expect(await app.page.evaluate(() => removeLayer(0))).toBe(false);
  expect(await app.hintText()).toMatch(/бумаг/i);
  expect((await stack(app)).names.length).toBe(1);
});

test('бумага остаётся снизу', async ({ page }) => {
  const app = await openApp(page);
  await addLayer(app);
  expect(await app.page.evaluate(() => moveLayer(0, 1))).toBe(false);
  expect(await app.hintText()).toMatch(/снизу/i);
});

test('перестановка слоёв меняет, что чем перекрыто', async ({ page }) => {
  const app = await openApp(page);
  await paintLayer(app, '#ff0000');
  await addLayer(app);
  await paintLayer(app, '#0000ff');
  await addLayer(app);
  await paintLayer(app, '#00ff00');
  expect(isNear(await app.pixel(450, 300), 0, 255, 0)).toBe(true);

  await app.page.evaluate(() => moveLayer(2, -1));
  expect(isNear(await app.pixel(450, 300), 0, 0, 255), 'после перестановки сверху должен быть синий').toBe(true);
});

test('удаление слоя не уводит кисть на чужой слой', async ({ page }) => {
  const app = await openApp(page);
  await addLayer(app);
  await addLayer(app);          // активен 2
  await app.page.evaluate(() => removeLayer(2));
  expect((await stack(app)).active).toBe(1);
});

// ─────────── Потолок ───────────

test('число слоёв ограничено, и предел зависит от размера холста', async ({ page }) => {
  const app = await openApp(page);
  const small = await app.page.evaluate(() => maxLayersFor(100, 100));
  const big = await app.page.evaluate(() => maxLayersFor(4000, 3000));
  expect(small).toBeGreaterThan(big);
  expect(big).toBeGreaterThanOrEqual(1);
  expect(small).toBeLessThanOrEqual(100);
});

test('упёршись в предел слоёв, приложение объясняется', async ({ page }) => {
  const app = await openApp(page);
  await app.page.evaluate(() => {
    document.getElementById('width-input').value = '4000';
    document.getElementById('height-input').value = '3000';
    resizeCanvas();
  });
  await app.settle();
  const limit = await app.page.evaluate(() => maxLayersFor(canvas.width, canvas.height));
  for (let i = 0; i < limit + 2; i++) {
    await app.page.evaluate(() => addLayer());
  }
  expect((await stack(app)).names.length).toBe(limit);
  expect(await app.hintText()).toMatch(/предел/i);
});

// ─────────── Стопка переживает преобразования документа ───────────

test('поворот, отражение, обрезка и смена размера не расплющивают стопку', async ({ page }) => {
  const app = await openApp(page);
  await paintLayer(app, '#ff0000');
  await addLayer(app);
  await app.page.evaluate(() => {
    const g = lctx();
    g.fillStyle = '#0000ff';
    g.fillRect(0, 0, 300, 200);
    composite();
  });

  for (const op of ['rotateCanvas()', 'flipH()', 'flipV()']) {
    await app.page.evaluate((code) => eval(code), op);
    await app.settle();
    expect((await stack(app)).names.length, op).toBe(2);
  }

  await app.pickTool('crop');
  await app.drag(50, 50, 400, 300);
  await app.page.evaluate(() => applyCrop());
  await app.settle();
  expect((await stack(app)).names.length, 'обрезка').toBe(2);

  await app.page.evaluate(() => {
    document.getElementById('width-input').value = '700';
    document.getElementById('height-input').value = '500';
    resizeCanvas();
  });
  await app.settle();
  const st = await stack(app);
  expect(st.names.length, 'смена размера').toBe(2);
  const sizes = await app.page.evaluate(() =>
    state.layers.map((l) => [l.canvas.width, l.canvas.height]));
  expect(sizes, 'слой обязан быть размером с документ').toEqual([[700, 500], [700, 500]]);
});

// ─────────── Лента помнит стопку ───────────

test('отмена возвращает и пиксели слоёв, и их свойства', async ({ page }) => {
  const app = await openApp(page);
  await addLayer(app);
  await paintLayer(app, '#0000ff');
  // paintLayer рисует напрямую, мимо ленты: без своей записи отмена вернулась бы к
  // кадру ДО заливки, и проверка мерила бы не то.
  await app.page.evaluate(() => saveHistory('Заливка слоя'));
  const before = await stack(app);
  const pic = await app.fingerprint();

  await app.page.evaluate(() => setLayerVisible(1, false));
  await app.page.evaluate(() => setLayerOpacity(1, 0.25));
  expect(await app.fingerprint()).not.toBe(pic);

  await app.undo();
  await app.undo();
  expect(await app.fingerprint()).toBe(pic);
  const after = await stack(app);
  expect(after.visible).toEqual(before.visible);
  expect(after.opacity).toEqual(before.opacity);
});

test('добавление и удаление слоя попадают в ленту по-русски и отменяются', async ({ page }) => {
  const app = await openApp(page);
  const n = (await app.history()).labels.length;
  await addLayer(app);
  const labels = (await app.history()).labels;
  expect(labels.length).toBe(n + 1);
  expect(labels[labels.length - 1]).toMatch(/[а-яё]/i);

  await app.undo();
  expect((await stack(app)).names.length).toBe(1);
});

test('«Создать» оставляет один слой и сбрасывает его свойства', async ({ page }) => {
  const app = await openApp(page);
  await addLayer(app);
  await app.page.evaluate(() => { setLayerVisible(0, false); });
  page.on('dialog', (d) => d.accept());
  await app.page.evaluate(() => newCanvas());
  await app.settle();

  const st = await stack(app);
  expect(st.names.length).toBe(1);
  expect(st.visible).toEqual([true]);
  expect(st.opacity).toEqual([1]);
  expect(isWhite(await app.pixel(450, 300))).toBe(true);
});

test('открытие файла заменяет стопку целиком', async ({ page }) => {
  const app = await openApp(page);
  await addLayer(app);
  await app.page.evaluate(() => setLayerVisible(0, false));

  const url = await app.page.evaluate(() => {
    const c = document.createElement('canvas');
    c.width = 200; c.height = 120;
    const g = c.getContext('2d');
    g.fillStyle = '#ff0000';
    g.fillRect(0, 0, 200, 120);
    return c.toDataURL();
  });
  page.on('dialog', (d) => d.accept());
  await app.page.evaluate((u) => openImageAsDocument(u, 'x.png'), url);
  await app.page.waitForFunction(() => canvas.width === 200);
  await app.settle();

  const st = await stack(app);
  expect(st.names.length).toBe(1);
  expect(st.visible).toEqual([true]);
  expect(isNear(await app.pixel(100, 60), 255, 0, 0), 'картинка легла в невидимый слой').toBe(true);
});

// ─────────── Панель слоёв ───────────

/** Строки панели сверху вниз: имя, активность, видимость. */
async function panelRows(app) {
  return app.page.evaluate(() => Array.from(document.querySelectorAll('.layer-row')).map((r) => ({
    index: Number(r.dataset.index),
    name: r.querySelector('.layer-name').textContent,
    active: r.classList.contains('active'),
    hidden: r.classList.contains('hidden'),
  })));
}

test('панель показывает стопку сверху вниз, как она лежит на холсте', async ({ page }) => {
  const app = await openApp(page);
  await addLayer(app);
  await addLayer(app);
  const rows = await panelRows(app);
  expect(rows.map((r) => r.index)).toEqual([2, 1, 0]);
});

test('панель отмечает активный слой', async ({ page }) => {
  const app = await openApp(page);
  await addLayer(app);
  expect((await panelRows(app)).filter((r) => r.active).map((r) => r.index)).toEqual([1]);

  await app.page.evaluate(() => setActiveLayer(0));
  expect((await panelRows(app)).filter((r) => r.active).map((r) => r.index)).toEqual([0]);
});

test('щелчок по строке делает слой активным', async ({ page }) => {
  const app = await openApp(page);
  await addLayer(app);
  await app.page.evaluate(() => {
    document.querySelector('.layer-row[data-index="0"]').click();
  });
  expect((await stack(app)).active).toBe(0);
});

test('щелчок по глазу прячет слой и не меняет активный', async ({ page }) => {
  const app = await openApp(page);
  await addLayer(app);          // активен 1
  await app.page.evaluate(() => {
    document.querySelector('.layer-row[data-index="0"] .layer-eye').click();
  });
  const st = await stack(app);
  expect(st.visible).toEqual([false, true]);
  expect(st.active, 'щелчок по глазу увёл активный слой').toBe(1);
  expect((await panelRows(app)).find((r) => r.index === 0).hidden).toBe(true);
});

test('ползунок непрозрачности показывает активный слой, а не последний тронутый', async ({ page }) => {
  const app = await openApp(page);
  await addLayer(app);
  await app.page.evaluate(() => setLayerOpacity(1, 0.4));
  expect(await app.page.evaluate(() => document.getElementById('layer-opacity').value)).toBe('40');

  await app.page.evaluate(() => setActiveLayer(0));
  expect(await app.page.evaluate(() => document.getElementById('layer-opacity').value),
    'ползунок держит чужое значение').toBe('100');
});

test('бумагу нельзя увести вниз, и кнопка это показывает', async ({ page }) => {
  const app = await openApp(page);
  await addLayer(app);
  const disabled = await app.page.evaluate(() => {
    const row = document.querySelector('.layer-row[data-index="0"]');
    return Array.from(row.querySelectorAll('.layer-move')).map((b) => b.disabled);
  });
  expect(disabled, 'у бумаги обе стрелки должны быть недоступны').toEqual([true, true]);
});

test('панель переживает отмену', async ({ page }) => {
  const app = await openApp(page);
  await addLayer(app);
  expect((await panelRows(app)).length).toBe(2);
  await app.undo();
  expect((await panelRows(app)).length).toBe(1);
});

// ─────────── Многослойная семантика (правила C# 1.12.0-1.20.0) ───────────
// Номер в скобках - пункт C#-выпуска, из которого правило взято.

// [066] Штрих ложится на слой, активный на ОТПУСКАНИИ, а не на нажатии
test('слой, сменённый посреди штриха, не уводит краску', async ({ page }) => {
  const app = await openApp(page);
  await addLayer(app);                       // активен 1
  await app.pickTool('pencil');
  await app.setColor('#000000');
  await app.setSize(20);

  const a = await app.toScreen(200, 300);
  const b = await app.toScreen(700, 300);
  await page.mouse.move(a.x, a.y);
  await page.mouse.down();
  await page.mouse.move(b.x, b.y, { steps: 6 });
  // Слой меняют посреди жеста - панель под рукой всё это время.
  await app.page.evaluate(() => { state.activeLayer = 0; });
  await page.mouse.up();
  await app.settle();

  const ink = await app.page.evaluate(() => state.layers.map((l) => {
    const d = l.canvas.getContext('2d').getImageData(450, 300, 1, 1).data;
    return d[3] > 0 && d[0] < 100;
  }));
  expect(ink[1], 'краска должна лечь туда, где жест начался').toBe(true);
  expect(ink[0], 'краска ушла в чужой слой').toBe(false);
});

// [111] Превью рисуется поверх ВСЕХ слоёв
test('превью штриха по нижнему слою не лежит поверх верхнего', async ({ page }) => {
  const app = await openApp(page);
  await addLayer(app);
  await paintLayer(app, '#0000ff');           // верхний слой сплошь синий
  await app.page.evaluate(() => setActiveLayer(0));

  await app.pickTool('pencil');
  await app.setColor('#ff0000');
  await app.setSize(30);
  const a = await app.toScreen(200, 300);
  const b = await app.toScreen(700, 300);
  await page.mouse.move(a.x, a.y);
  await page.mouse.down();
  await page.mouse.move(b.x, b.y, { steps: 6 });
  // Пока кнопка не отпущена, штрих по бумаге НЕ должен быть виден: сверху сплошной слой.
  // Читаем ровно то, что видит пользователь: сборку. Отдельный холст превью на время
  // мазка спрятан, и складывать его сюда ещё раз значило бы мерить не то.
  const during = await app.page.evaluate(() => ({
    pixel: Array.from(ctx.getImageData(450, 300, 1, 1).data),
    previewHidden: getComputedStyle(previewCanvas).visibility === 'hidden',
  }));
  await page.mouse.up();
  await app.settle();
  expect(during.previewHidden, 'отдельный холст превью не спрятан').toBe(true);
  expect(isNear(during.pixel, 0, 0, 255), 'превью вылезло поверх верхнего слоя: ' + JSON.stringify(during)).toBe(true);
});

// [095] Вставка на скрытый слой
test('вставка на скрытый слой не проходит молча', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('pencil');
  await app.setSize(14);
  await app.drag(200, 200, 500, 400);
  await app.page.evaluate(() => copySelection());

  await addLayer(app);
  await app.page.evaluate(() => setLayerVisible(1, false));
  const n = (await app.history()).labels.length;
  await app.page.evaluate(() => pasteFromClipboard());
  await app.settle();

  const st = await app.page.evaluate(() => ({ floating: !!state.floating, n: state.history.length }));
  const hinted = await app.hintText();
  expect(!st.floating && hinted !== '', JSON.stringify({ st, hinted, n })).toBe(true);
});

// [103] Delete по скрытому слою
test('Delete по скрытому слою не стирает невидимое молча', async ({ page }) => {
  const app = await openApp(page);
  await addLayer(app);
  await paintLayer(app, '#0000ff');
  await app.pickTool('select');
  await app.drag(200, 200, 500, 400);
  await app.page.evaluate(() => setLayerVisible(1, false));

  const before = await app.page.evaluate(() =>
    state.layers[1].canvas.toDataURL());
  await page.keyboard.press('Delete');
  await app.settle();
  const after = await app.page.evaluate(() => state.layers[1].canvas.toDataURL());
  const hinted = await app.hintText();
  expect(after === before || hinted !== '', JSON.stringify({ changed: after !== before, hinted })).toBe(true);
});

// [022] Поднятая картинка видна, даже если её слой прозрачный
test('поднятое в руках видно, даже когда слой прозрачен', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('pencil');
  await app.setColor('#000000');
  await app.setSize(20);
  await app.drag(200, 200, 500, 400);
  await app.pickTool('select');
  await app.drag(180, 180, 520, 420);
  await app.clickAt(350, 300);
  expect(await app.page.evaluate(() => !!state.floating), 'подъём не случился').toBe(true);

  await app.page.evaluate(() => setLayerOpacity(0, 0));
  const visible = await app.page.evaluate(() => {
    const d = previewCtx.getImageData(300, 280, 60, 40).data;
    for (let i = 3; i < d.length; i += 4) if (d[i] > 0) return true;
    return false;
  });
  expect(visible, 'то, что пользователь держит, спрятали').toBe(true);
});

// [052] Отмена удаления слоя не уводит кисть
test('отмена удаления слоя возвращает активным тот же слой', async ({ page }) => {
  const app = await openApp(page);
  await addLayer(app);
  await addLayer(app);
  await app.page.evaluate(() => setActiveLayer(1));
  const before = await app.page.evaluate(() => state.activeLayer);

  await app.page.evaluate(() => removeLayer(2));
  await app.undo();
  expect(await app.page.evaluate(() => state.activeLayer), 'кисть уехала на другой слой').toBe(before);
});

// [094] Подъём с верхнего слоя не должен терять белое
test('подъём с верхнего слоя не съедает белое нарисованное', async ({ page }) => {
  const app = await openApp(page);
  await addLayer(app);
  // На верхнем слое - белый квадрат внутри чёрной рамки.
  await app.page.evaluate(() => {
    const g = lctx();
    g.fillStyle = '#000000';
    g.fillRect(200, 200, 300, 200);
    g.fillStyle = '#ffffff';
    g.fillRect(250, 250, 200, 100);
    composite();
    saveHistory('Рисунок');
  });

  await app.pickTool('select');
  await app.drag(190, 190, 510, 410);
  await app.clickAt(350, 300);
  expect(await app.page.evaluate(() => !!state.floating), 'подъём не случился').toBe(true);

  const hasWhite = await app.page.evaluate(() => {
    const f = state.floating;
    const d = f.canvas.getContext('2d').getImageData(0, 0, f.canvas.width, f.canvas.height).data;
    for (let i = 0; i < d.length; i += 4) {
      if (d[i + 3] > 200 && d[i] > 240 && d[i + 1] > 240 && d[i + 2] > 240) return true;
    }
    return false;
  });
  expect(hasWhite, 'белое нарисованное выкусили вместе с фоном').toBe(true);
});

// Заливка на верхнем слое не должна красить его целиком
test('заливка на верхнем слое кладёт только залитое', async ({ page }) => {
  const app = await openApp(page);
  await addLayer(app);
  await app.pickTool('fill');
  await app.setColor('#00aa00');
  await app.clickAt(450, 300);

  // Бумага под верхним слоем обязана остаться белой и нетронутой.
  const paperWhite = await app.page.evaluate(() => {
    const d = state.layers[0].canvas.getContext('2d').getImageData(450, 300, 1, 1).data;
    return d[0] > 245 && d[1] > 245 && d[2] > 245;
  });
  expect(paperWhite, 'заливка залезла в чужой слой').toBe(true);
  expect(isNear(await app.pixel(450, 300), 0, 170, 0)).toBe(true);
});

// Сохранение в файл берёт сборку, а не активный слой
test('сохраняемая картинка - это сборка, а не один слой', async ({ page }) => {
  const app = await openApp(page);
  await paintLayer(app, '#ff0000');
  await addLayer(app);
  await app.page.evaluate(() => {
    const g = lctx(); g.fillStyle = '#0000ff'; g.fillRect(0, 0, 300, 200); composite();
  });
  const url = await app.page.evaluate(() => canvas.toDataURL());
  expect(url).toBe(await app.fingerprint());
  expect(isNear(await app.pixel(150, 100), 0, 0, 255)).toBe(true);
  expect(isNear(await app.pixel(700, 450), 255, 0, 0)).toBe(true);
});
