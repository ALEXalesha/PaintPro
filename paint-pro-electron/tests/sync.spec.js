// Выпуск 1.16.0: раскладка и возможности, выровненные по WPF-версии 1.28.0. Инструменты
// слева с подписями, правая панель в том же порядке, «Последние» цвета, поворот против
// часовой, флажок «Заливать фигуры», окна вопросов в цветах темы, горячие клавиши на
// любой раскладке.

const { test, expect } = require('@playwright/test');
const { openApp, isWhite, isNear } = require('./harness');

// ─────────── Раскладка ───────────

test('все девятнадцать инструментов слева, у каждого подпись, и все видны без прокрутки', async ({ page }) => {
  await page.setViewportSize({ width: 1400, height: 900 });
  const app = await openApp(page);
  const r = await app.page.evaluate(() => {
    const panel = document.querySelector('.tools-panel');
    const tools = [...panel.querySelectorAll('.tool')];
    const pr = panel.getBoundingClientRect();
    return {
      n: tools.length,
      unlabeled: tools.filter(t => !(t.querySelector('.tool-label') || {}).textContent).map(t => t.dataset.tool),
      clipped: tools.filter(t => t.getBoundingClientRect().bottom > pr.bottom).map(t => t.dataset.tool),
      noToolbar: !document.querySelector('.toolbar'),
      leftOfCanvas: pr.right <= document.getElementById('canvas-area').getBoundingClientRect().left,
    };
  });
  expect(r.n).toBe(19);
  expect(r.unlabeled).toEqual([]);
  expect(r.clipped).toEqual([]);
  expect(r.noToolbar).toBe(true);
  expect(r.leftOfCanvas).toBe(true);
});

test('подписи инструментов не вылезают из кнопок', async ({ page }) => {
  const app = await openApp(page);
  const over = await app.page.evaluate(() =>
    [...document.querySelectorAll('.tool-label')]
      .filter(l => l.scrollWidth > l.parentElement.clientWidth)
      .map(l => l.textContent));
  expect(over).toEqual([]);
});

test('правая панель идёт в порядке WPF-версии', async ({ page }) => {
  const app = await openApp(page);
  const titles = await app.page.evaluate(() =>
    [...document.querySelectorAll('.sidebar .sidebar-title')].map(t => t.textContent.trim().toLowerCase()));
  const order = ['цвет', 'последние', 'размер', 'прозрачность', 'холст', 'быстрые действия', 'слои'];
  const idx = order.map(o => titles.findIndex(t => t.startsWith(o)));
  expect(idx.every(i => i >= 0), JSON.stringify(titles)).toBe(true);
  expect([...idx].sort((a, b) => a - b)).toEqual(idx);
});

test('поле выбора цвета занимает весь образец, а не его левую часть', async ({ page }) => {
  const app = await openApp(page);
  const [a, b] = await app.page.evaluate(() =>
    ['color-preview', 'color-input'].map(id => {
      const r = document.getElementById(id).getBoundingClientRect();
      return [Math.round(r.left), Math.round(r.top), Math.round(r.width), Math.round(r.height)];
    }));
  // Рамка образца - 1.5 px с каждой стороны: поле лежит внутри неё.
  expect(Math.abs(a[2] - b[2])).toBeLessThanOrEqual(4);
  expect(Math.abs(a[3] - b[3])).toBeLessThanOrEqual(4);
  // И щелчок в правый край попадает именно в поле.
  const hit = await app.page.evaluate(() => {
    const r = document.getElementById('color-preview').getBoundingClientRect();
    return document.elementFromPoint(r.right - 5, r.top + r.height / 2).id;
  });
  expect(hit).toBe('color-input');
});

test('палитра - та же, что в WPF-версии: сорок цветов по восемь в ряд', async ({ page }) => {
  const app = await openApp(page);
  const r = await app.page.evaluate(() => {
    const sw = [...document.querySelectorAll('#palette .color-swatch')];
    const tops = new Set(sw.map(s => s.offsetTop)); // выбранный образец увеличен transform-ом
    const pal = document.getElementById('palette').getBoundingClientRect();
    const first = sw[1].getBoundingClientRect(), last8 = sw[7].getBoundingClientRect();
    const cell = sw[2].getBoundingClientRect().left - first.left;
    return { n: sw.length, rows: tops.size, left: first.left - cell - pal.left, right: pal.right - last8.right };
  });
  expect(r.n).toBe(40);
  expect(r.rows).toBe(5);
  // Сетка по центру: зазоры слева и справа равны.
  expect(Math.abs(r.left - r.right)).toBeLessThanOrEqual(1);
});

// ─────────── Последние цвета ───────────

test('последние цвета: свежий первым, без повторов, не больше восьми', async ({ page }) => {
  const app = await openApp(page);
  const seq = ['#ff0000', '#00ff00', '#0000ff', '#ff0000', '#111111', '#222222', '#333333',
               '#444444', '#555555', '#666666', '#777777'];
  for (const c of seq) await app.setColor(c);
  const recent = await app.page.evaluate(() => recentColors.slice());
  expect(recent).toEqual(['#777777', '#666666', '#555555', '#444444', '#333333', '#222222', '#111111', '#ff0000']);
  const shown = await app.page.evaluate(() =>
    [...document.querySelectorAll('#recent-colors .color-swatch')].map(s => s.dataset.tip.toLowerCase()));
  expect(shown).toEqual(recent);
});

test('тот же цвет ещё раз не двигает последние', async ({ page }) => {
  const app = await openApp(page);
  await app.setColor('#ff0000');
  await app.setColor('#00ff00');
  await app.setColor('#00ff00');
  expect(await app.page.evaluate(() => recentColors.slice())).toEqual(['#00ff00', '#ff0000']);
});

test('щелчок по последнему цвету берёт его', async ({ page }) => {
  const app = await openApp(page);
  await app.setColor('#123456');
  await app.setColor('#654321');
  await app.page.locator('#recent-colors .color-swatch').nth(1).click();
  expect(await app.page.evaluate(() => state.color.toLowerCase())).toBe('#123456');
});

test('пока цвет подбирают в окне, в последние не сыплются промежуточные оттенки', async ({ page }) => {
  const app = await openApp(page);
  await app.page.evaluate(() => {
    const input = document.getElementById('color-input');
    input.dispatchEvent(new MouseEvent('click'));
    for (const c of ['#100000', '#200000', '#300000']) {
      input.value = c;
      input.dispatchEvent(new Event('input'));
    }
    input.dispatchEvent(new Event('change'));
  });
  expect(await app.page.evaluate(() => state.color.toLowerCase())).toBe('#300000');
  expect(await app.page.evaluate(() => recentColors.slice())).toEqual(['#300000']);
});

test('окно выбора цвета закрыли с тем же цветом - в последние ничего', async ({ page }) => {
  const app = await openApp(page);
  await app.page.evaluate(() => {
    const input = document.getElementById('color-input');
    input.dispatchEvent(new MouseEvent('click'));
    input.dispatchEvent(new Event('change'));
  });
  expect(await app.page.evaluate(() => recentColors.length)).toBe(0);
});

test('пипетка кладёт взятый цвет в последние', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('picker');
  await app.clickAt(100, 100);
  expect(await app.page.evaluate(() => recentColors[0])).toBe('#ffffff');
});

// ─────────── Поворот ───────────

async function marked(app) {
  // Красная точка в левом верхнем углу, холст 900 × 600.
  await app.page.evaluate(() => {
    const g = state.layers[0].canvas.getContext('2d');
    g.fillStyle = '#ff0000';
    g.fillRect(0, 0, 30, 30);
    composite();
    saveHistory('Метка');
  });
}

test('поворот против часовой уносит левый верхний угол в левый нижний', async ({ page }) => {
  const app = await openApp(page);
  await marked(app);
  await app.page.evaluate(() => rotateCanvasCCW());
  await app.settle();
  expect(await app.page.evaluate(() => [canvas.width, canvas.height])).toEqual([600, 900]);
  expect(isNear(await app.pixel(10, 890), 255, 0, 0)).toBe(true);
  expect(isWhite(await app.pixel(10, 10))).toBe(true);
  expect(await app.page.evaluate(() => state.history[state.historyIndex].label)).toBe('Поворот против часовой');
});

test('поворот по часовой и обратно возвращает картинку', async ({ page }) => {
  const app = await openApp(page);
  await marked(app);
  const before = await app.fingerprint();
  await app.page.evaluate(() => rotateCanvas());
  expect(await app.page.evaluate(() => state.history[state.historyIndex].label)).toBe('Поворот по часовой');
  await app.page.evaluate(() => rotateCanvasCCW());
  await app.settle();
  expect(await app.page.evaluate(() => [canvas.width, canvas.height])).toEqual([900, 600]);
  expect(await app.fingerprint()).toBe(before);
});

test('кнопки поворота есть и в меню, и в быстрых действиях', async ({ page }) => {
  const app = await openApp(page);
  const r = await app.page.evaluate(() => ({
    menu: [...document.querySelectorAll('.mb-dd-item')].map(b => b.textContent.trim()),
    quick: [...document.querySelectorAll('.quick-grid .side-btn')].map(b => b.getAttribute('onclick')),
  }));
  expect(r.menu).toContain('Повернуть 90° по часовой');
  expect(r.menu).toContain('Повернуть 90° против часовой');
  expect(r.quick).toEqual(['rotateCanvas()', 'rotateCanvasCCW()', 'flipH()', 'flipV()']);
});

// ─────────── Заливка фигур ───────────

test('флажок «Заливать фигуры» заливает прямоугольник, снятый - нет', async ({ page }) => {
  const app = await openApp(page);
  await app.setColor('#0000ff');
  await app.setSize(4);
  await app.page.check('#fill-shapes');
  expect(await app.page.evaluate(() => state.fillMode)).toBe('both');
  await app.pickTool('rect');
  await app.drag(100, 100, 300, 250);
  expect(isNear(await app.pixel(200, 175), 0, 0, 255)).toBe(true);

  await app.page.uncheck('#fill-shapes');
  expect(await app.page.evaluate(() => state.fillMode)).toBe('outline');
  await app.drag(400, 100, 600, 250);
  expect(isWhite(await app.pixel(500, 175))).toBe(true);
  expect(isNear(await app.pixel(400, 175), 0, 0, 255)).toBe(true);
});

test('флажок следует за режимом, заданным из кода', async ({ page }) => {
  const app = await openApp(page);
  await app.page.evaluate(() => setFillMode('solid'));
  expect(await app.page.isChecked('#fill-shapes')).toBe(true);
  await app.page.evaluate(() => setFillMode('outline'));
  expect(await app.page.isChecked('#fill-shapes')).toBe(false);
});

// ─────────── Окна вопросов ───────────

test('«Очистить холст» спрашивает окном темы, а не системным', async ({ page }) => {
  const app = await openApp(page);
  let native = 0;
  page.on('dialog', (d) => { native++; d.dismiss().catch(() => {}); });
  await app.pickTool('pencil');
  await app.drag(100, 100, 300, 300);
  const drawn = await app.fingerprint();

  await app.page.evaluate(() => { clearCanvas(); });
  await expect(app.page.locator('.gm-box')).toBeVisible();
  expect(await app.page.locator('.gm-title').textContent()).toBe('Очистка холста');
  await app.answer('Нет');
  expect(await app.fingerprint()).toBe(drawn);

  await app.page.evaluate(() => { clearCanvas(); });
  await app.answer('Да');
  expect(await app.fingerprint()).not.toBe(drawn);
  expect(native).toBe(0);
});

test('в окне вопроса Escape - «Нет», Enter - «Да», а клавиши редактора молчат', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('pencil');
  await app.drag(100, 100, 300, 300);
  const drawn = await app.fingerprint();

  await app.page.evaluate(() => { window.__r = clearCanvas(); });
  await page.keyboard.press('e');            // ластик выбран не будет
  await page.keyboard.press('Escape');
  await app.page.evaluate(() => window.__r);
  expect(await app.page.evaluate(() => state.tool)).toBe('pencil');
  expect(await app.fingerprint()).toBe(drawn);

  await app.page.evaluate(() => { window.__r = clearCanvas(); });
  await page.keyboard.press('Enter');
  await app.page.evaluate(() => window.__r);
  await app.settle();
  expect(await app.fingerprint()).not.toBe(drawn);
});

test('ошибка размера холста - окно темы с текстом', async ({ page }) => {
  const app = await openApp(page);
  await app.page.fill('#width-input', '1');
  await app.page.evaluate(() => resizeCanvas());
  expect(await app.page.locator('.gm-text').textContent()).toContain('Размер должен быть от');
  await app.answer('OK');
  expect(await app.page.evaluate(() => canvas.width)).toBe(900);
});

test('окно вопроса читается во всех темах', async ({ page }) => {
  const app = await openApp(page);
  const bad = [];
  for (const id of await app.page.evaluate(() => THEMES.map(t => t.id))) {
    await app.page.evaluate((t) => setTheme(t), id);
    await app.page.evaluate(() => { glassAlert('Проверка'); });
    const d = await app.page.evaluate(() => {
      const box = document.querySelector('.gm-box');
      const cs = getComputedStyle(box);
      const rgb = (c) => String(c).match(/[\d.]+/g).map(Number);
      const lum = ([r, g, b]) => (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255;
      return Math.abs(lum(rgb(cs.color)) - lum(rgb(cs.backgroundColor)));
    });
    if (d < 0.4) bad.push({ id, d });
    await app.answer('OK');
  }
  expect(bad).toEqual([]);
});

// ─────────── Горячие клавиши ───────────

test('Ctrl+Z отменяет и на русской раскладке', async ({ page }) => {
  const app = await openApp(page);
  const blank = await app.fingerprint();
  await app.pickTool('pencil');
  await app.drag(100, 100, 300, 300);
  await app.page.evaluate(() => document.dispatchEvent(new KeyboardEvent('keydown',
    { key: 'я', code: 'KeyZ', ctrlKey: true, bubbles: true })));
  await app.settle();
  expect(await app.fingerprint()).toBe(blank);
});

test('буквы инструментов работают на русской раскладке', async ({ page }) => {
  const app = await openApp(page);
  await app.page.evaluate(() => document.dispatchEvent(new KeyboardEvent('keydown',
    { key: 'и', code: 'KeyB', bubbles: true })));
  expect(await app.page.evaluate(() => state.tool)).toBe('brush');
});

test('L, R, O и C - те же инструменты, что в WPF-версии', async ({ page }) => {
  const app = await openApp(page);
  for (const [k, t] of [['l', 'line'], ['r', 'rect'], ['o', 'ellipse'], ['c', 'crop']]) {
    await page.keyboard.press(k);
    expect(await app.page.evaluate(() => state.tool), k).toBe(t);
  }
});

test('«Правка → Удалить» стирает выделенное, как Delete', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('pencil');
  await app.setSize(20);
  await app.drag(100, 100, 300, 100);
  await app.pickTool('select');
  await app.drag(80, 80, 320, 130);
  await app.page.evaluate(() => deleteSelectionContent());
  await app.settle();
  expect(isWhite(await app.pixel(200, 100))).toBe(true);
  expect(await app.page.evaluate(() => state.history[state.historyIndex].label)).toBe('Удаление');
});

test('кнопки масштаба лежат над холстом и не заходят на панели', async ({ page }) => {
  for (const size of [{ width: 1400, height: 900 }, { width: 900, height: 600 }]) {
    await page.setViewportSize(size);
    const app = await openApp(page);
    const r = await app.page.evaluate(() => {
      const box = (s) => document.querySelector(s).getBoundingClientRect();
      const z = box('.zoom-controls'), area = box('#canvas-area'), side = box('.sidebar');
      return { inside: z.left >= area.left && z.right <= area.right && z.bottom <= area.bottom,
               clear: z.right <= side.left };
    });
    expect(r, JSON.stringify(size)).toEqual({ inside: true, clear: true });
  }
});

// ─────────── Ширина боковых панелей ───────────

async function dragGrip(page, id, dx) {
  const b = await page.locator('#' + id).boundingBox();
  const x = b.x + b.width / 2, y = b.y + b.height / 2;
  await page.mouse.move(x, y);
  await page.mouse.down();
  await page.mouse.move(x + dx, y, { steps: 6 });
  await page.mouse.up();
}

// Событие resize приходит к странице к следующему кадру: сначала дождаться его.
const widths = async (page) => (await page.evaluate(() => new Promise(r => requestAnimationFrame(() => r()))), page.evaluate(() => ({
  left: Math.round(document.querySelector('.tools-panel').getBoundingClientRect().width),
  right: Math.round(document.querySelector('.sidebar').getBoundingClientRect().width),
  canvas: Math.round(document.getElementById('canvas-area').getBoundingClientRect().width),
})));

// Всё, что в панели, - в её пределах по горизонтали (кроме прокручиваемого содержимого ленты).
const overflow = (page, sel) => page.evaluate((s) => {
  const panel = document.querySelector(s);
  // Внутренняя область: без рамки, полей и полосы прокрутки (clientWidth её не считает).
  const cs = getComputedStyle(panel);
  const box = panel.getBoundingClientRect();
  const left = box.left + panel.clientLeft + parseFloat(cs.paddingLeft);
  const pr = { left, right: box.left + panel.clientLeft + panel.clientWidth - parseFloat(cs.paddingRight) };
  const out = [];
  for (const el of panel.querySelectorAll('*')) {
    const r = el.getBoundingClientRect();
    if (!r.width || !r.height || getComputedStyle(el).visibility === 'hidden') continue;
    if (el.closest('.history-list, .layers-list') && el !== el.closest('.history-list, .layers-list')) continue;
    if (r.left < pr.left - 0.5 || r.right > pr.right + 0.5) out.push((el.id || el.className || el.tagName) + ' ' + Math.round(r.left) + '..' + Math.round(r.right));
  }
  return out;
}, sel);

test('левую панель сужают до одного столбца и расширяют до трёх, ничего не налезает', async ({ page }) => {
  await page.setViewportSize({ width: 1400, height: 900 });
  const app = await openApp(page);
  await dragGrip(page, 'grip-left', -200);
  expect((await widths(page)).left).toBe(88);
  const cols = () => page.evaluate(() => new Set([...document.querySelectorAll('.tool-grid')[0].children]
    .map(b => Math.round(b.getBoundingClientRect().left))).size);
  expect(await cols()).toBe(1);
  expect(await overflow(page, '.tools-panel')).toEqual([]);
  await dragGrip(page, 'grip-left', 400);
  expect((await widths(page)).left).toBe(208);
  expect(await cols()).toBe(3);
  expect(await overflow(page, '.tools-panel')).toEqual([]);
});

test('правую панель сужают и расширяют в пределах, палитра и кнопки остаются внутри', async ({ page }) => {
  await page.setViewportSize({ width: 1400, height: 900 });
  const app = await openApp(page);
  await dragGrip(page, 'grip-right', 300);
  expect((await widths(page)).right).toBe(260);
  expect(await overflow(page, '.sidebar')).toEqual([]);
  // Низкое окно - у панели появляется полоса прокрутки и отнимает ширину у содержимого.
  await page.setViewportSize({ width: 1400, height: 650 });
  expect(await page.evaluate(() => { const s = document.querySelector('.sidebar'); return s.scrollHeight > s.clientHeight; })).toBe(true);
  expect(await overflow(page, '.sidebar')).toEqual([]);
  await page.setViewportSize({ width: 1400, height: 900 });
  const rows = await page.evaluate(() => new Set([...document.querySelectorAll('#palette .color-swatch')].map(s => s.offsetTop)).size);
  expect(rows).toBe(5);
  await dragGrip(page, 'grip-right', -400);
  expect((await widths(page)).right).toBe(420);
  expect(await overflow(page, '.sidebar')).toEqual([]);
});

test('ширина панелей запоминается, двойной щелчок по краю возвращает как было', async ({ page }) => {
  await page.setViewportSize({ width: 1400, height: 900 });
  const app = await openApp(page);
  await dragGrip(page, 'grip-left', -40);
  await dragGrip(page, 'grip-right', -60);
  const w = await widths(page);
  expect(w.left).toBe(102);
  expect(w.right).toBe(350);
  await page.reload();
  await page.waitForFunction(() => typeof state !== 'undefined');
  expect(await widths(page)).toMatchObject({ left: 102, right: 350 });
  await page.locator('#grip-left').dblclick();
  await page.locator('#grip-right').dblclick();
  expect(await widths(page)).toMatchObject({ left: 142, right: 290 });
});

test('на узком окне панели уступают холсту, а шире - возвращаются', async ({ page }) => {
  await page.setViewportSize({ width: 1600, height: 900 });
  const app = await openApp(page);
  await dragGrip(page, 'grip-left', 200);
  await dragGrip(page, 'grip-right', -300);
  expect(await widths(page)).toMatchObject({ left: 208, right: 420 });
  await page.setViewportSize({ width: 900, height: 700 });
  const narrow = await widths(page);
  expect(narrow.canvas).toBeGreaterThanOrEqual(300 - 1);
  expect(narrow.right).toBeLessThan(420);
  expect(await overflow(page, '.sidebar')).toEqual([]);
  await page.setViewportSize({ width: 1600, height: 900 });
  expect(await widths(page)).toMatchObject({ left: 208, right: 420 });
});

test('правило ширины: что бы ни просили, панели в пределах и холсту хватает места', async ({ page }) => {
  const app = await openApp(page);
  const bad = await page.evaluate(() => {
    const out = [];
    let seed = 1280;
    const rnd = () => { seed = (seed * 1103515245 + 12345) % 2147483648; return seed / 2147483648; };
    for (let i = 0; i < 5000; i++) {
      const l = rnd() * 600 - 100, r = rnd() * 800 - 100, w = 300 + rnd() * 3000;
      const [fl, fr] = fitPanels(l, r, w);
      const fits = w - PANELS.chrome - PANELS.canvasMin >= PANELS.leftMin + PANELS.rightMin;
      const again = fitPanels(fl, fr, w);
      if (fl < PANELS.leftMin || fl > PANELS.leftMax || fr < PANELS.rightMin || fr > PANELS.rightMax
          || (fits && w - PANELS.chrome - fl - fr < PANELS.canvasMin - 1e-9)
          || again[0] !== fl || again[1] !== fr) out.push([l, r, w, fl, fr]);
    }
    return out.slice(0, 3);
  });
  expect(bad).toEqual([]);
});
