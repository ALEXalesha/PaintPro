// Темы оформления.
//
// Тема - это набор CSS-переменных. Всё оформление ходит через них, поэтому смена сводится
// к атрибуту на <html>. Проверять тут надо не «красиво ли», а два правила, нарушение
// которых делает приложение неработоспособным:
//
//   1. текст обязан быть виден на фоне СВОЕЙ темы;
//   2. то, что всплывает над ХОЛСТОМ, обязано оставаться тёмным в ЛЮБОЙ теме - бумага
//      белая всегда, и стеклянная подложка над ней белеет.
//
// Второе правило уже стоило одной находки (подсказка, панель обрезки и кнопки масштаба
// были нечитаемы), и темы - ровно тот случай, когда его легко нарушить заново.

const { test, expect } = require('@playwright/test');
const { openApp, isWhite } = require('./harness');

const IDS = ['glass', 'formal', 'light', 'night', 'warm'];

/** Яркость цвета из строки вида rgb()/rgba()/#rrggbb. */
function luminance(css) {
  let r, g, b;
  const m = String(css).match(/rgba?\(([^)]+)\)/);
  if (m) {
    const p = m[1].split(',').map(Number);
    [r, g, b] = p;
  } else {
    const h = String(css).trim().replace('#', '');
    r = parseInt(h.slice(0, 2), 16);
    g = parseInt(h.slice(2, 4), 16);
    b = parseInt(h.slice(4, 6), 16);
  }
  return (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255;
}

async function tokens(app) {
  return app.page.evaluate(() => {
    const cs = getComputedStyle(document.documentElement);
    const get = (n) => cs.getPropertyValue(n).trim();
    return {
      theme: document.documentElement.getAttribute('data-theme') || 'glass',
      text: get('--text'),
      base: get('--app-base'),
      overlayBg: get('--overlay-bg'),
      overlayText: get('--overlay-text'),
      accent: get('--accent'),
    };
  });
}

test('по умолчанию стоит исходная тема', async ({ page }) => {
  const app = await openApp(page);
  expect(await app.page.evaluate(() => currentTheme())).toBe('glass');
  expect(await app.page.evaluate(() => document.documentElement.getAttribute('data-theme'))).toBeNull();
});

test('все пять тем объявлены и переключаются', async ({ page }) => {
  const app = await openApp(page);
  expect(await app.page.evaluate(() => THEMES.map((t) => t.id))).toEqual(IDS);
  for (const id of IDS) {
    expect(await app.page.evaluate((x) => setTheme(x), id), id).toBe(id);
    expect(await app.page.evaluate(() => currentTheme()), id).toBe(id);
  }
});

test('у каждой темы своё оформление, а не одно и то же', async ({ page }) => {
  const app = await openApp(page);
  const seen = [];
  for (const id of IDS) {
    await app.page.evaluate((x) => setTheme(x), id);
    const t = await tokens(app);
    seen.push(t.base + '|' + t.text + '|' + t.accent);
  }
  expect(new Set(seen).size, 'темы совпали между собой: ' + JSON.stringify(seen)).toBe(IDS.length);
});

test('в каждой теме текст виден на её собственном фоне', async ({ page }) => {
  const app = await openApp(page);
  const bad = [];
  for (const id of IDS) {
    await app.page.evaluate((x) => setTheme(x), id);
    const t = await tokens(app);
    const d = Math.abs(luminance(t.text) - luminance(t.base));
    if (d < 0.4) bad.push({ id, text: t.text, base: t.base, разница: +d.toFixed(2) });
  }
  expect(bad, 'текст сливается с фоном: ' + JSON.stringify(bad)).toEqual([]);
});

test('подложка над холстом остаётся тёмной в любой теме', async ({ page }) => {
  // Холст белый всегда. Если тема перекрасит --overlay-bg в светлое, подсказка снова
  // станет белым по белому - ровно то, что уже случалось.
  const app = await openApp(page);
  const bad = [];
  for (const id of IDS) {
    await app.page.evaluate((x) => setTheme(x), id);
    const t = await tokens(app);
    const d = Math.abs(luminance(t.overlayBg) - luminance(t.overlayText));
    if (luminance(t.overlayBg) > 0.35 || d < 0.4) {
      bad.push({ id, фон: t.overlayBg, текст: t.overlayText });
    }
  }
  expect(bad, 'подложка над холстом посветлела: ' + JSON.stringify(bad)).toEqual([]);
});

test('правило про всплывающее над холстом держится во всех темах', async ({ page }) => {
  // То же, что в appearance.spec.js, но прогнанное по каждой теме.
  const app = await openApp(page);
  const bad = [];
  for (const id of IDS) {
    await app.page.evaluate((x) => setTheme(x), id);
    const rows = await app.page.evaluate(() => {
      const over = ['.hint', '.crop-controls', '.zoom-controls', '.resize-tooltip', '.text-toolbar'];
      const parse = (c) => {
        const m = String(c).match(/rgba?\(([^)]+)\)/);
        if (!m) return null;
        const p = m[1].split(',').map(Number);
        return { r: p[0], g: p[1], b: p[2], a: p.length > 3 ? p[3] : 1 };
      };
      const lum = (c) => (0.2126 * c.r + 0.7152 * c.g + 0.0722 * c.b) / 255;
      const out = [];
      for (const n of over) {
        const el = document.querySelector(n);
        if (!el) continue;
        const bg = parse(getComputedStyle(el).backgroundColor);
        const fg = parse(getComputedStyle(el).color);
        if (!bg || !fg) continue;
        const overWhite = {
          r: bg.r * bg.a + 255 * (1 - bg.a),
          g: bg.g * bg.a + 255 * (1 - bg.a),
          b: bg.b * bg.a + 255 * (1 - bg.a),
        };
        if (Math.abs(lum(overWhite) - lum(fg)) < 0.4) out.push(n);
      }
      return out;
    });
    if (rows.length) bad.push({ id, элементы: rows });
  }
  expect(bad, 'над белым холстом текст сливается с подложкой: ' + JSON.stringify(bad)).toEqual([]);
});

test('строка меню читается в каждой теме', async ({ page }) => {
  // Строка меню стоит на фоне ОКНА, а не над холстом: её текст обязан идти от темы.
  // Пока он был зашит белым, светлая тема делала «Файл», «Правка» и «Вид» невидимыми.
  const app = await openApp(page);
  const bad = [];
  for (const id of IDS) {
    await app.page.evaluate((x) => setTheme(x), id);
    // У пунктов меню плавный переход цвета: сразу после смены темы getComputedStyle
    // отдаёт промежуточное значение анимации, а не конечное.
    await app.page.waitForTimeout(250);
    const t = await tokens(app);
    const c = await app.page.evaluate(() =>
      getComputedStyle(document.querySelector('.mb-item')).color);
    if (Math.abs(luminance(c) - luminance(t.base)) < 0.4) bad.push({ id, цвет: c, фон: t.base });
  }
  expect(bad, 'строка меню сливается с фоном: ' + JSON.stringify(bad)).toEqual([]);
});

test('выпадающее меню читается в каждой теме', async ({ page }) => {
  // Список всплывает и может лечь на холст - значит живёт по правилу подложек.
  const app = await openApp(page);
  const bad = [];
  for (const id of IDS) {
    await app.page.evaluate((x) => setTheme(x), id);
    await app.page.waitForTimeout(250);
    const r = await app.page.evaluate(() => ({
      bg: getComputedStyle(document.querySelector('.mb-dropdown')).backgroundColor,
      fg: getComputedStyle(document.querySelector('.mb-dd-item')).color,
    }));
    if (Math.abs(luminance(r.bg) - luminance(r.fg)) < 0.4) bad.push({ id, ...r });
  }
  expect(bad, 'выпадающее меню сливается: ' + JSON.stringify(bad)).toEqual([]);
});

test('выбор темы переживает перезапуск', async ({ page }) => {
  const app = await openApp(page);
  await app.page.evaluate(() => setTheme('warm'));
  await page.reload();
  await page.waitForFunction(() => typeof state !== 'undefined');
  expect(await page.evaluate(() => currentTheme()), 'тема не запомнилась').toBe('warm');
});

test('неизвестная тема откатывается к исходной', async ({ page }) => {
  // Файл настроек могли поправить руками. Приложение без оформления - не приложение.
  const app = await openApp(page);
  expect(await app.page.evaluate(() => setTheme('такой-нет'))).toBe('glass');
  expect(await app.page.evaluate(() => currentTheme())).toBe('glass');
});

test('меню отмечает выбранную тему', async ({ page }) => {
  const app = await openApp(page);
  await app.page.evaluate(() => setTheme('night'));
  const marked = await app.page.evaluate(() =>
    Array.from(document.querySelectorAll('#theme-menu .mb-dd-item'))
      .filter((b) => b.querySelector('.theme-mark').textContent.trim() !== '')
      .map((b) => b.dataset.theme));
  expect(marked).toEqual(['night']);
});

test('щелчок по пункту меню меняет тему', async ({ page }) => {
  const app = await openApp(page);
  await app.page.evaluate(() => {
    document.querySelector('#theme-menu .mb-dd-item[data-theme="formal"]').click();
  });
  expect(await app.page.evaluate(() => currentTheme())).toBe('formal');
});

test('смена темы не трогает ни рисунок, ни ленту', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('pencil');
  await app.setColor('#ff0000');
  await app.setSize(20);
  await app.drag(200, 200, 700, 400);
  // Штрих законно делает документ изменённым - помечаем сохранённым, иначе проверка
  // мерила бы не смену темы, а свой же штрих.
  await app.page.evaluate(() => {
    state.savedHistoryIndex = state.historyIndex;
    state.savedDisabledKey = disabledKey();
  });
  const pic = await app.fingerprint();
  const n = (await app.history()).labels.length;

  for (const id of IDS) await app.page.evaluate((x) => setTheme(x), id);
  await app.settle();

  expect(await app.fingerprint(), 'смена темы изменила рисунок').toBe(pic);
  expect((await app.history()).labels.length, 'смена темы попала в ленту').toBe(n);
  expect(await app.isDirty(), 'смена темы объявила документ изменённым').toBe(false);
});

test('холст остаётся белой бумагой в любой теме', async ({ page }) => {
  const app = await openApp(page);
  for (const id of IDS) {
    await app.page.evaluate((x) => setTheme(x), id);
    const p = await app.pixel(450, 300);
    expect(p[0] > 245 && p[1] > 245 && p[2] > 245, id + ': бумага перекрасилась').toBe(true);
  }
});

// ─── Полное перечисление: кто ещё обязан знать про тему ───

test('во всех темах панели читаются, а не только строка меню', async ({ page }) => {
  const app = await openApp(page);
  const bad = [];
  for (const id of IDS) {
    await app.page.evaluate((x) => setTheme(x), id);
    await app.page.waitForTimeout(250);
    const rows = await app.page.evaluate(() => {
      const sels = ['.sidebar-title', '.info-row', '.layer-name', '.hist-item',
                    '.tool', '.mb-app', '.mb-right', '.statusbar'];
      const parse = (c) => {
        const m = String(c).match(/rgba?\(([^)]+)\)/);
        if (!m) return null;
        const p = m[1].split(',').map(Number);
        return { r: p[0], g: p[1], b: p[2], a: p.length > 3 ? p[3] : 1 };
      };
      const lum = (c) => (0.2126 * c.r + 0.7152 * c.g + 0.0722 * c.b) / 255;
      const base = parse(getComputedStyle(document.body).backgroundColor)
        || { r: 18, g: 16, b: 31, a: 1 };
      const out = [];
      for (const s of sels) {
        const el = document.querySelector(s);
        if (!el) continue;
        const cs = getComputedStyle(el);
        const fg = parse(cs.color);
        if (!fg) continue;
        // Сравнивать надо с ТЕМ, на чём элемент лежит. У выделенной строки ленты своя
        // сплошная заливка акцентом, и белый текст на ней - это правильно; сравнение с
        // фоном окна дало бы ложную тревогу.
        const own = parse(cs.backgroundColor);
        const under = own && own.a > 0.75 ? own : base;
        if (Math.abs(lum(fg) - lum(under)) < 0.25) out.push(s);
      }
      return out;
    });
    if (rows.length) bad.push({ id, элементы: rows });
  }
  expect(bad, 'текст сливается с фоном окна: ' + JSON.stringify(bad)).toEqual([]);
});

test('обводка активного слоя видна в каждой теме', async ({ page }) => {
  const app = await openApp(page);
  const bad = [];
  for (const id of IDS) {
    await app.page.evaluate((x) => setTheme(x), id);
    await app.page.waitForTimeout(250);
    const same = await app.page.evaluate(() => {
      const row = document.querySelector('.layer-row.active');
      if (!row) return null;
      const cs = getComputedStyle(row);
      return cs.borderColor === 'rgba(0, 0, 0, 0)' || cs.borderColor === cs.backgroundColor;
    });
    if (same) bad.push(id);
  }
  expect(bad, 'активный слой не выделен: ' + JSON.stringify(bad)).toEqual([]);
});

// ─── Тема и работа документа ───

test('тема не влияет на то, что попадает в файл', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('pencil');
  await app.setColor('#ff0000');
  await app.setSize(20);
  await app.drag(200, 200, 700, 400);
  const pic = await app.page.evaluate(() => canvas.toDataURL());

  for (const id of IDS) {
    await app.page.evaluate((x) => setTheme(x), id);
    await app.settle();
    expect(await app.page.evaluate(() => canvas.toDataURL()), id).toBe(pic);
  }
});

test('смена темы посреди жеста не роняет и не теряет штрих', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('pencil');
  await app.setColor('#000000');
  await app.setSize(20);
  const a = await app.toScreen(200, 300);
  const b = await app.toScreen(700, 300);
  await page.mouse.move(a.x, a.y);
  await page.mouse.down();
  await page.mouse.move(b.x, b.y, { steps: 6 });
  await app.page.evaluate(() => setTheme('night'));
  await page.mouse.up();
  await app.settle();
  expect(isWhite(await app.pixel(450, 300)), 'штрих пропал').toBe(false);
  expect(await app.page.evaluate(() => canvas.width)).toBe(900);
});

test('тема переживает «Создать» и открытие файла', async ({ page }) => {
  const app = await openApp(page);
  await app.page.evaluate(() => setTheme('warm'));
  page.on('dialog', (d) => d.accept().catch(() => {}));
  await app.page.evaluate(() => newCanvas());
  await app.settle();
  expect(await app.page.evaluate(() => currentTheme())).toBe('warm');

  const url = await app.page.evaluate(() => {
    const c = document.createElement('canvas');
    c.width = 200; c.height = 150;
    const g = c.getContext('2d');
    g.fillStyle = '#00aa00';
    g.fillRect(0, 0, 200, 150);
    return c.toDataURL();
  });
  await app.page.evaluate((u) => openImageAsDocument(u, 'x.png'), url);
  await app.page.waitForFunction(() => canvas.width === 200);
  expect(await app.page.evaluate(() => currentTheme())).toBe('warm');
});

test('битое значение в памяти выбора не ломает запуск', async ({ page }) => {
  const app = await openApp(page);
  await app.page.evaluate(() => localStorage.setItem('paint-pro-theme', 'мусор'));
  await page.reload();
  await page.waitForFunction(() => typeof state !== 'undefined');
  expect(await page.evaluate(() => currentTheme())).toBe('glass');
  expect(await page.evaluate(() => [canvas.width, canvas.height])).toEqual([900, 600]);
});

test('частая смена темы не копит мусор в разметке', async ({ page }) => {
  const app = await openApp(page);
  const before = await app.page.evaluate(() => document.querySelectorAll('#theme-menu .mb-dd-item').length);
  for (let i = 0; i < 20; i++) {
    await app.page.evaluate((x) => setTheme(x), IDS[i % IDS.length]);
  }
  const after = await app.page.evaluate(() => document.querySelectorAll('#theme-menu .mb-dd-item').length);
  expect(after, 'пункты меню размножились').toBe(before);
});

// ─── Браузерный путь ───

test('без хранилища тема не роняет приложение', async ({ page }) => {
  // Приватный режим и файловый протокол в некоторых браузерах бросают на localStorage.
  const app = await openApp(page);
  const ok = await app.page.evaluate(() => {
    const orig = Object.getOwnPropertyDescriptor(window, 'localStorage');
    try {
      Object.defineProperty(window, 'localStorage', {
        configurable: true,
        get() { throw new Error('нет хранилища'); },
      });
      setTheme('night');
      return currentTheme();
    } finally {
      if (orig) Object.defineProperty(window, 'localStorage', orig);
    }
  });
  expect(ok).toBe('night');
});

test('подсказка про уход со страницы не мешает в Electron', async ({ page }) => {
  // Мост подменён - значит это «Electron», и своего вопроса браузера быть не должно:
  // спрашивает main-процесс, иначе пользователь получит два окна подряд.
  const app = await openApp(page);
  await app.pickTool('pencil');
  await app.setSize(20);
  await app.drag(200, 200, 500, 400);
  const prevented = await app.page.evaluate(() => {
    const e = new Event('beforeunload', { cancelable: true });
    window.dispatchEvent(e);
    return e.defaultPrevented;
  });
  expect(prevented, 'в Electron браузер спросил бы второй раз').toBe(false);
});
