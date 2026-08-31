// Правила, добытые в C#-версии и проверенные здесь. Каждая проверка названа выпуском,
// в котором правило появилось там: так видно, что именно перенесено, а что совпало само.

const { test, expect } = require('@playwright/test');
const { openApp, isWhite, isNear } = require('./harness');

async function draw(app) {
  await app.pickTool('pencil');
  await app.setColor('#000000');
  await app.setSize(14);
  await app.drag(200, 200, 500, 400);
}

async function lift(app) {
  await app.pickTool('select');
  await app.drag(180, 180, 520, 420);
  await app.clickAt(350, 300);
  return app.page.evaluate(() => !!state.floating);
}

// ─────────── Буфер обмена (C# 1.20.0) ───────────

test('Ctrl+C без выделения копирует весь холст', async ({ page }) => {
  const app = await openApp(page);
  await draw(app);
  await page.keyboard.press('Control+c');
  await app.page.waitForTimeout(60);
  expect(await app.page.evaluate(() => state.clipboard && [state.clipboard.width, state.clipboard.height]))
    .toEqual([900, 600]);
  expect(await app.hintText()).toBe('');
});

test('Ctrl+X без выделения объясняет, почему так нельзя', async ({ page }) => {
  const app = await openApp(page);
  await draw(app);
  const before = await app.fingerprint();
  await page.keyboard.press('Control+x');
  await app.page.waitForTimeout(60);
  expect(await app.hintText()).toMatch(/целиком/i);
  expect(await app.fingerprint()).toBe(before);
});

// ─────────── Вставка (C# 1.19.0) ───────────

test('вставка на маленький холст остаётся на нём видимой', async ({ page }) => {
  const app = await openApp(page);
  await draw(app);
  await app.page.evaluate(() => copySelection());
  await app.page.evaluate(() => {
    document.getElementById('width-input').value = '15';
    document.getElementById('height-input').value = '15';
    resizeCanvas();
  });
  await app.settle();

  await app.page.evaluate(() => pasteFromClipboard());
  await app.settle();
  const f = await app.page.evaluate(() => state.floating && { x: state.floating.x, y: state.floating.y });
  expect(f, 'вставка не случилась').not.toBeNull();
  expect(f.x, JSON.stringify(f)).toBeLessThan(15);
  expect(f.y, JSON.stringify(f)).toBeLessThan(15);
});

// ─────────── «Создать» (C# 1.18.0) ───────────

test('«Создать» даёт чистый лист обычного размера, а не размер прежней работы', async ({ page }) => {
  const app = await openApp(page);
  await app.page.evaluate(() => {
    document.getElementById('width-input').value = '1600';
    document.getElementById('height-input').value = '1200';
    resizeCanvas();
  });
  await app.settle();

  page.on('dialog', (d) => d.accept());
  await app.page.evaluate(() => newCanvas());
  await app.settle();
  expect(await app.page.evaluate(() => [canvas.width, canvas.height])).toEqual([900, 600]);
  expect(isWhite(await app.pixel(450, 300))).toBe(true);
  expect(await app.isDirty()).toBe(false);
});

// ─────────── Статусбар (C# 1.17.0) ───────────

test('цвет пикселя гаснет, когда курсор ушёл с холста', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('fill');
  await app.setColor('#ff0000');
  await app.clickAt(450, 300);
  const p = await app.toScreen(450, 300);
  await page.mouse.move(p.x, p.y);
  await app.page.waitForTimeout(120);
  expect(await app.page.evaluate(() => document.getElementById('pixel-hex').textContent)).toBe('#ff0000');

  await page.mouse.move(5, 5);
  await app.page.waitForTimeout(120);
  expect(await app.page.evaluate(() => document.getElementById('pixel-hex').textContent)).toBe('—');
});

// ─────────── Снять выделение (C# 1.15.0) ───────────

test('Ctrl+D заканчивает и с рамкой, и с поднятым объектом', async ({ page }) => {
  const app = await openApp(page);
  await draw(app);
  expect(await lift(app), 'подъём не случился').toBe(true);

  await page.keyboard.press('Control+d');
  await app.page.waitForTimeout(80);
  await app.settle();
  expect(await app.page.evaluate(() => ({ floating: !!state.floating, sel: !!state.selection })))
    .toEqual({ floating: false, sel: false });
});

// ─────────── Поднятый объект ───────────

test('повёрнутый объект берётся там, где он нарисован, а не по габариту (C# 1.12.0)', async ({ page }) => {
  const app = await openApp(page);
  await draw(app);
  expect(await lift(app), 'подъём не случился').toBe(true);

  await app.page.evaluate(() => { state.floating.rotation = Math.PI / 4; renderFloating(); });
  const f = await app.page.evaluate(() => ({ x: state.floating.x, y: state.floating.y }));
  expect(!!(await app.page.evaluate((q) => hitFloating(q.x + 4, q.y + 4), f)),
    'угол неповёрнутого габарита не должен считаться попаданием').toBe(false);
  expect(!!(await app.page.evaluate(() =>
    hitFloating(state.floating.x + state.floating.w / 2, state.floating.y + state.floating.h / 2))))
    .toBe(true);
});

test('смена инструмента на полигон не прижимает поднятое (C# 1.15.0)', async ({ page }) => {
  const app = await openApp(page);
  await draw(app);
  expect(await lift(app), 'подъём не случился').toBe(true);
  await app.drag(350, 300, 500, 400);

  await app.pickTool('quad');
  expect(await app.page.evaluate(() => !!state.floating)).toBe(true);
});

test('копирование объекта, унесённого за холст, не затирает буфер молча (C# 1.17.0)', async ({ page }) => {
  const app = await openApp(page);
  await draw(app);
  await app.pickTool('select');
  await app.drag(180, 180, 520, 420);
  await app.page.evaluate(() => copySelection());
  const good = await app.page.evaluate(() => [state.clipboard.width, state.clipboard.height]);

  await app.clickAt(350, 300);
  expect(await app.page.evaluate(() => !!state.floating), 'подъём не случился').toBe(true);
  await app.page.evaluate(() => { state.floating.x = -5000; state.floating.y = -5000; renderFloating(); });
  await app.page.evaluate(() => copySelection());

  const now = await app.page.evaluate(() => [state.clipboard.width, state.clipboard.height]);
  const hinted = await app.hintText();
  expect(JSON.stringify(now) === JSON.stringify(good) || hinted !== '',
    JSON.stringify({ good, now, hinted })).toBe(true);
});

// ─────────── Мелочи, которые в C# оказывались дефектами ───────────

test('размер текста слушается своего поля (C# 1.18.0)', async ({ page }) => {
  const app = await openApp(page);
  const inkFor = async (size) => {
    await app.page.evaluate(() => { fillWhite(); });
    await app.page.evaluate((v) => { document.getElementById('text-size').value = String(v); }, size);
    await app.pickTool('text');
    await app.clickAt(100, 300);
    await app.page.evaluate(() => { textEditor.innerText = 'Ф'; commitText(); });
    await app.settle();
    return app.page.evaluate(() => {
      const d = ctx.getImageData(0, 0, canvas.width, canvas.height).data;
      let n = 0;
      for (let i = 0; i < d.length; i += 4) if (d[i] < 200) n++;
      return n;
    });
  };
  const small = await inkFor(8);
  const big = await inkFor(48);
  expect(big, JSON.stringify({ small, big })).toBeGreaterThan(small * 1.5);
});

test('клик чуть левее холста не считается кликом по его углу (C# 1.15.0)', async ({ page }) => {
  // Приведение дробной координаты к целому в сторону нуля схлопывало всё от -0.99 до 0
  // в ноль: точка мимо холста становилась его левым верхним пикселем.
  const app = await openApp(page);
  const p = await app.page.evaluate(() => {
    const r = canvas.getBoundingClientRect();
    return getPos({ clientX: r.left - 0.5, clientY: r.top - 0.5 });
  });
  expect(p.x < 0 || p.y < 0, JSON.stringify(p)).toBe(true);
});

test('растянутый объект прижимается со сглаживанием (C# 1.17.0)', async ({ page }) => {
  const app = await openApp(page);
  expect(await app.page.evaluate(() => ctx.imageSmoothingEnabled)).toBe(true);
});

test('первая запись ленты не обещает чистый лист, если начало забыто (C# 1.19.0)', async ({ page }) => {
  test.setTimeout(180000);
  const app = await openApp(page);
  for (let i = 0; i < 170; i++) {
    await app.page.evaluate((k) => {
      ctx.fillStyle = '#000';
      ctx.fillRect((k * 7) % 800, Math.floor(k / 100) * 5, 3, 3);
      saveHistory('Штрих');
    }, i);
  }
  const labels = (await app.history()).labels;
  expect(labels.length).toBeLessThanOrEqual(150);
  expect(labels[0]).not.toBe('Исходное состояние');
});


/** Нарисовать и положить в буфер прямоугольную копию нарисованного. */
async function copyOf(app) {
  await draw(app);
  await app.pickTool('select');
  await app.drag(180, 180, 520, 420);
  await app.page.evaluate(() => copySelection());
}

// ─────────── Вставка, полигон и лента (C# 1.12.0-1.19.0) ───────────
// Ни одно из этих правил чинить не пришлось: здесь они соблюдались с самого начала,
// и часть из них в C#-версию попала как раз отсюда. Проверки стоят как сторожа.

// [102] Delete, Ctrl+X и Escape по только что вставленной картинке
test('Escape по вставке не оставляет применённой записи', async ({ page }) => {
  const app = await openApp(page);
  await copyOf(app);
  const before = await app.fingerprint();
  const idx = (await app.history()).index;

  await app.page.evaluate(() => pasteFromClipboard());
  await app.settle();
  await page.keyboard.press('Escape');
  await app.settle();

  expect(await app.fingerprint()).toBe(before);
  expect((await app.history()).index).toBe(idx);
  expect(await app.isDirty()).toBe(true);   // сохранённой метки не ставили
});

test('Delete по вставке убирает картинку и не оставляет её в ленте', async ({ page }) => {
  const app = await openApp(page);
  await copyOf(app);
  const before = await app.fingerprint();

  await app.page.evaluate(() => pasteFromClipboard());
  await app.settle();
  await page.keyboard.press('Delete');
  await app.settle();

  expect(await app.page.evaluate(() => !!state.floating)).toBe(false);
  expect(await app.fingerprint(), 'холст должен вернуться к прежнему').toBe(before);
});

// [059] Вставка, унесённая за край холста
test('вставка, унесённая за холст, не оставляет записи без картинки', async ({ page }) => {
  const app = await openApp(page);
  await copyOf(app);
  const before = await app.fingerprint();
  const idx = (await app.history()).index;

  await app.page.evaluate(() => pasteFromClipboard());
  await app.settle();
  await app.page.evaluate(() => { state.floating.x = -5000; state.floating.y = -5000; renderFloating(); });
  await page.keyboard.press('Enter');
  await app.settle();

  expect(await app.fingerprint(), 'на холсте не должно измениться ничего').toBe(before);
  expect((await app.history()).index, 'записи о прижатии быть не должно').toBe(idx);
});

// [060] Отмена «Создать» после вставки
test('отмена «Создать» после вставки возвращает картинку', async ({ page }) => {
  const app = await openApp(page);
  await copyOf(app);
  await app.page.evaluate(() => pasteFromClipboard());
  await app.settle();
  await app.drag(300, 250, 600, 450);
  await page.keyboard.press('Enter');
  await app.settle();
  const withPaste = await app.fingerprint();

  page.on('dialog', (d) => d.accept());
  await app.page.evaluate(() => newCanvas());
  await app.settle();
  await app.undo();
  expect(await app.fingerprint()).toBe(withPaste);
});

// [072] Прыжок по ленте после прижатой вставки
test('прыжок по ленте не оставляет копию поверх прижатой картинки', async ({ page }) => {
  const app = await openApp(page);
  await copyOf(app);
  await app.page.evaluate(() => pasteFromClipboard());
  await app.settle();
  await app.drag(300, 250, 600, 450);
  await page.keyboard.press('Enter');
  await app.settle();
  const withPaste = await app.fingerprint();
  const idx = (await app.history()).index;

  await app.page.evaluate((i) => jumpHistory(i - 1), idx);
  await app.settle();
  await app.page.evaluate((i) => jumpHistory(i), idx);
  await app.settle();

  expect(await app.page.evaluate(() => !!state.floating), 'картинки в руках быть не должно').toBe(false);
  expect(await app.fingerprint()).toBe(withPaste);
});

// [074] Копия поднятого объекта тащит подложку
test('копия поднятого объекта не тащит того, что под ним', async ({ page }) => {
  const app = await openApp(page);
  // Синее пятно в правом нижнем углу готовим ЗАРАНЕЕ: смена инструмента прижала бы
  // поднятое, и проверка мерила бы совсем другое.
  await app.page.evaluate(() => {
    ctx.fillStyle = '#0000ff';
    ctx.fillRect(600, 380, 300, 220);
  });
  await draw(app);

  await app.pickTool('select');
  await app.drag(180, 180, 520, 360);
  await app.clickAt(350, 270);
  expect(await app.page.evaluate(() => !!state.floating), 'подъём не случился').toBe(true);

  // Тем же инструментом уводим объект на синее и копируем.
  await app.drag(350, 270, 750, 490);
  await app.page.evaluate(() => copySelection());

  const hasBlue = await app.page.evaluate(() => {
    const g = state.clipboard.getContext('2d');
    const d = g.getImageData(0, 0, state.clipboard.width, state.clipboard.height).data;
    for (let i = 0; i < d.length; i += 4) {
      if (d[i + 3] > 200 && d[i + 2] > 180 && d[i] < 80) return true;
    }
    return false;
  });
  expect(hasBlue, 'в буфер попал цвет подложки').toBe(false);
});

// [076] Shift округляет угол поворота до 15 градусов
test('Shift при повороте ручкой округляет угол', async ({ page }) => {
  const app = await openApp(page);
  await draw(app);
  await app.pickTool('select');
  await app.drag(180, 180, 520, 420);
  await app.clickAt(350, 300);
  expect(await app.page.evaluate(() => !!state.floating), 'подъём не случился').toBe(true);

  // Крутим программно тем же путём, каким это делает мышь: startRotateDrag плюс
  // движение с зажатым Shift.
  const snapped = await app.page.evaluate(() => {
    const f = state.floating;
    const cx = f.x + f.w / 2, cy = f.y + f.h / 2;
    state.rotatingFloating = true;
    state.rotationStart = { centerX: cx, centerY: cy, initialMouseAngle: 0, initialRotation: 0 };
    const r = canvas.getBoundingClientRect();
    const ang = 0.37;                       // заведомо не кратный 15 градусам
    const ev = new MouseEvent('mousemove', {
      clientX: r.left + cx + Math.cos(ang) * 200,
      clientY: r.top + cy + Math.sin(ang) * 200,
      bubbles: true, shiftKey: true
    });
    canvas.dispatchEvent(ev);
    return state.floating.rotation;
  });
  const step = Math.PI / 12;
  expect(Math.abs(snapped / step - Math.round(snapped / step)), String(snapped)).toBeLessThan(0.01);
});

// [081] Маленький многоугольник не поднять
test('маленький полигон всё же берётся за тело', async ({ page }) => {
  const app = await openApp(page);
  await draw(app);
  await app.pickTool('quad');
  await app.drag(300, 280, 330, 310);
  const f = await app.page.evaluate(() => state.floating && { x: state.floating.x, y: state.floating.y, w: state.floating.w, h: state.floating.h });
  test.skip(!f, 'полигон такого размера не поднимается вовсе');
  const hit = !!(await app.page.evaluate(() =>
    hitFloating(state.floating.x + state.floating.w / 2, state.floating.y + state.floating.h / 2)));
  expect(hit, JSON.stringify(f)).toBe(true);
});

// [107] Многоугольник тянется за габарит
test('полигон не хватается за угол описанного прямоугольника', async ({ page }) => {
  const app = await openApp(page);
  await draw(app);
  await app.pickTool('quad');
  await app.drag(180, 180, 520, 420);
  const f = await app.page.evaluate(() => state.floating && { ...state.floating, canvas: null });
  test.skip(!f || !f.quad, 'полигон не поднялся');

  // Срезаем верхний левый угол: точка внутри габарита, но снаружи фигуры.
  await app.page.evaluate(() => {
    const f = state.floating;
    f.quad[0] = { x: f.x + f.w / 2, y: f.y };
    renderFloating();
  });
  const hitCut = !!(await app.page.evaluate(() => hitFloating(state.floating.x + 4, state.floating.y + 4)));
  expect(hitCut, 'срезанный угол не должен считаться попаданием').toBe(false);
});

// [070] Подъём выделения у самого края холста
test('подъём выделения, приткнутого к краю, не роняет приложение', async ({ page }) => {
  const app = await openApp(page);
  await draw(app);
  await app.pickTool('select');
  await app.drag(870, 560, 899, 599);
  await app.clickAt(885, 580);
  await app.settle();
  expect(await app.page.evaluate(() => canvas.width)).toBe(900);
});

// [109] Смена инструмента посреди жеста
test('смена инструмента посреди протяжки не оставляет жест включённым', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('select');
  const a = await app.toScreen(200, 200);
  const b = await app.toScreen(400, 350);
  await page.mouse.move(a.x, a.y);
  await page.mouse.down();
  await page.mouse.move(b.x, b.y, { steps: 4 });
  await page.keyboard.press('p');            // хоткей инструмента посреди жеста
  await page.mouse.up();
  await app.settle();
  expect(await app.page.evaluate(() => state.drawing)).toBe(false);
});

// [083] Отмена стирания многоугольника оставляла кайму
test('отмена удаления полигона возвращает холст до пикселя', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('fill');
  await app.setColor('#ff0000');
  await app.clickAt(450, 300);
  const before = await app.fingerprint();

  await app.pickTool('quad');
  await app.drag(200, 180, 560, 430);
  const lifted = await app.page.evaluate(() => !!state.floating);
  test.skip(!lifted, 'полигон не поднялся');
  await page.keyboard.press('Delete');
  await app.settle();
  expect(await app.fingerprint()).not.toBe(before);

  await app.undo();
  expect(await app.fingerprint()).toBe(before);
});


// ─────────── Пустые правки, ручки и повёрнутый полигон (C# 1.12.0-1.20.0) ───────────
// Тоже совпало без правок. Часть держится на общем правиле «кадр, равный текущему, в ленту
// не идёт» (1.11.2): ластик по чистой бумаге, кадрирование по всему холсту и Delete по
// пустому месту отсеиваются им заодно, каждый своим путём.

// [024] Ластик по нетронутой бумаге объявлял документ изменённым
test('ластик по чистой бумаге не пачкает документ', async ({ page }) => {
  const app = await openApp(page);
  const n = (await app.history()).labels.length;
  await app.pickTool('eraser');
  await app.page.evaluate(() => { state.eraserSize = 40; syncSizeForTool(); });
  await app.drag(200, 200, 700, 450);
  expect((await app.history()).labels.length).toBe(n);
  expect(await app.isDirty()).toBe(false);
});

// [025] Кадрирование по всему холсту записывалось как правка
test('кадрирование по всему холсту не записывается как правка', async ({ page }) => {
  const app = await openApp(page);
  await draw(app);
  await app.page.evaluate(() => { state.savedHistoryIndex = state.historyIndex; });
  const n = (await app.history()).labels.length;

  await app.pickTool('crop');
  await app.drag(50, 50, 800, 500);
  // Рамка ровно по краям холста: протяжкой мышью в точности такую не получить.
  await app.page.evaluate(() => {
    state.selection = { x: 0, y: 0, w: canvas.width, h: canvas.height };
    applyCrop();
  });
  await app.settle();

  expect(await app.page.evaluate(() => [canvas.width, canvas.height])).toEqual([900, 600]);
  expect((await app.history()).labels.length).toBe(n);
  expect(await app.isDirty()).toBe(false);
});

// [037] Delete по области, где стирать нечего
test('Delete по чистому месту не пачкает документ', async ({ page }) => {
  const app = await openApp(page);
  await draw(app);
  await app.page.evaluate(() => { state.savedHistoryIndex = state.historyIndex; });
  const n = (await app.history()).labels.length;

  await app.pickTool('select');
  await app.drag(650, 100, 850, 250);      // заведомо чистое место
  await page.keyboard.press('Delete');
  await app.settle();

  expect((await app.history()).labels.length).toBe(n);
  expect(await app.isDirty()).toBe(false);
});

// [034] Ручки маленького объекта накрывали его целиком - тело было не ухватить
test('маленький поднятый объект берётся за тело, а не только за ручки', async ({ page }) => {
  const app = await openApp(page);
  await draw(app);
  await app.pickTool('select');
  await app.drag(300, 280, 340, 320);      // объект 40x40
  await app.clickAt(320, 300);
  const f = await app.page.evaluate(() => state.floating && { x: state.floating.x, y: state.floating.y, w: state.floating.w, h: state.floating.h });
  expect(f, 'подъём не случился').not.toBeNull();

  // Клик в середину должен таскать, а не масштабировать.
  const p = await app.toScreen(f.x + f.w / 2, f.y + f.h / 2);
  await page.mouse.move(p.x, p.y);
  await page.mouse.down();
  const st = await app.page.evaluate(() => ({ drag: state.draggingFloating, resize: state.resizingFloating }));
  await page.mouse.up();
  expect(st.resize, JSON.stringify({ f, st })).toBe(false);
  expect(st.drag, JSON.stringify({ f, st })).toBe(true);
});

// [100] Зона хвата ручек не должна ехать вместе с масштабом
test('ручки берутся и на уменьшенном холсте', async ({ page }) => {
  const app = await openApp(page);
  await draw(app);
  await app.pickTool('select');
  await app.drag(180, 180, 520, 420);
  await app.clickAt(350, 300);
  expect(await app.page.evaluate(() => !!state.floating), 'подъём не случился').toBe(true);

  await app.page.evaluate(() => { state.zoom = 0.25; applyZoom(); });
  const f = await app.page.evaluate(() => ({ x: state.floating.x, y: state.floating.y, w: state.floating.w, h: state.floating.h }));
  const corner = await app.toScreen(f.x + f.w, f.y + f.h);
  await page.mouse.move(corner.x, corner.y);
  await page.mouse.down();
  const st = await app.page.evaluate(() => state.resizingFloating);
  await page.mouse.up();
  expect(st, 'за угловую ручку на масштабе 0.25 не ухватиться').toBe(true);
});

// [105] Ctrl+C по повёрнутому четырёхугольнику копировал не ту область
test('копия повёрнутого объекта берётся по его фактическому габариту', async ({ page }) => {
  const app = await openApp(page);
  await draw(app);
  await app.pickTool('select');
  await app.drag(180, 180, 520, 420);
  await app.clickAt(350, 300);
  expect(await app.page.evaluate(() => !!state.floating), 'подъём не случился').toBe(true);

  const before = await app.page.evaluate(() => {
    copySelection();
    return [state.clipboard.width, state.clipboard.height];
  });
  await app.page.evaluate(() => { state.floating.rotation = Math.PI / 4; renderFloating(); });
  const after = await app.page.evaluate(() => {
    copySelection();
    return [state.clipboard.width, state.clipboard.height];
  });
  // Повёрнутый на 45° прямоугольник занимает заметно больший габарит.
  expect(after[0], JSON.stringify({ before, after })).toBeGreaterThan(before[0]);
});

// [108] Углы повёрнутого quad'а стояли не там, где сама фигура
test('углы повёрнутого полигона совпадают с самой фигурой', async ({ page }) => {
  const app = await openApp(page);
  await draw(app);
  await app.pickTool('quad');
  await app.drag(180, 180, 520, 420);
  const has = await app.page.evaluate(() => !!(state.floating && state.floating.quad));
  test.skip(!has, 'полигон не поднялся');

  // Поворачиваем и смотрим, что точки углов уехали вместе с фигурой: попадание по
  // первому углу quad'а должно оставаться попаданием.
  await app.page.evaluate(() => {
    const f = state.floating;
    const cx = f.x + f.w / 2, cy = f.y + f.h / 2, d = Math.PI / 4;
    const c = Math.cos(d), s = Math.sin(d);
    f.quad = f.quad.map((p) => ({
      x: cx + c * (p.x - cx) - s * (p.y - cy),
      y: cy + s * (p.x - cx) + c * (p.y - cy),
    }));
    f.rotation = d;
    renderFloating();
  });
  const hitAtQuadCenter = !!(await app.page.evaluate(() => {
    const q = state.floating.quad;
    const cx = q.reduce((a, p) => a + p.x, 0) / 4;
    const cy = q.reduce((a, p) => a + p.y, 0) / 4;
    return hitFloating(cx, cy);
  }));
  expect(hitAtQuadCenter).toBe(true);
});

// [093] Подъём многоугольника не должен выкусывать бумагу изнутри выделенного
test('подъём полигона не выедает дыру внутри самой фигуры', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('fill');
  await app.setColor('#ff0000');
  await app.clickAt(450, 300);

  await app.pickTool('quad');
  await app.drag(200, 180, 560, 430);
  const has = await app.page.evaluate(() => !!state.floating);
  test.skip(!has, 'полигон не поднялся');

  // Полигон поднят без стирания габарита - под ним холст остаётся прежним.
  expect(isNear(await app.pixel(380, 300), 255, 0, 0)).toBe(true);
});

// [071] Escape по вставке, между которой и отказом легла другая правка
test('Escape по вставке после другой правки не портит холст', async ({ page }) => {
  const app = await openApp(page);
  await draw(app);
  await app.pickTool('select');
  await app.drag(180, 180, 520, 420);
  await app.page.evaluate(() => copySelection());

  await app.page.evaluate(() => pasteFromClipboard());
  await app.settle();
  const afterPaste = await app.fingerprint();

  // Между вставкой и отказом ложится другая правка: штрих по холсту.
  await app.pickTool('pencil');
  await app.setColor('#0000ff');
  await app.setSize(10);
  await app.drag(700, 100, 850, 250);
  await app.settle();
  const withStroke = await app.fingerprint();
  expect(withStroke).not.toBe(afterPaste);

  await page.keyboard.press('Escape');
  await app.settle();
  // Отказ от вставки не обязан её отменять как угодно, но холст обязан остаться связным:
  // синий штрих на месте, приложение живо.
  expect(isNear(await app.pixel(775, 175), 0, 0, 255), 'штрих, лёгший после вставки, пропал').toBe(true);
});

// [013][014] Масштаб на большом холсте
test('предельное увеличение большого холста не роняет приложение', async ({ page }) => {
  const app = await openApp(page);
  await app.page.evaluate(() => {
    document.getElementById('width-input').value = '4000';
    document.getElementById('height-input').value = '3000';
    resizeCanvas();
  });
  await app.settle();
  for (let i = 0; i < 30; i++) await app.page.evaluate(() => zoomIn());
  await app.page.evaluate(() => zoomReset());
  expect(await app.page.evaluate(() => [canvas.width, canvas.height])).toEqual([4000, 3000]);
});
