// Внешний вид: то, что видно только глазами - и то из него, что удаётся выразить правилом.
//
// Обе находки здесь пришли из запуска приложения руками, а не из чтения кода: одна портила
// работу (боковая панель уезжала при каждом штрихе), вторая делала подсказку нечитаемой.
// Но обе, найденные, выражаются проверкой - и дальше уже не вернутся.

const { test, expect } = require('@playwright/test');
const { openApp } = require('./harness');

test('рисование не утаскивает боковую панель', async ({ page }) => {
  // Лента истории прокручивается к текущей записи. Пока это делал scrollIntoView, он тащил
  // за собой ВСЕ прокручиваемые родители: боковая панель длинная, и каждый штрих уводил её
  // вниз - панель слоёв уезжала из виду прямо во время рисования.
  const app = await openApp(page);
  await app.page.evaluate(() => { document.querySelector('.sidebar').scrollTop = 0; });
  const before = await app.page.evaluate(() => document.querySelector('.sidebar').scrollTop);

  await app.pickTool('pencil');
  await app.setSize(10);
  for (let i = 0; i < 6; i++) await app.drag(150, 100 + i * 40, 700, 120 + i * 40);

  const after = await app.page.evaluate(() => document.querySelector('.sidebar').scrollTop);
  expect(after, 'панель уехала: было ' + before + ', стало ' + after).toBe(before);
});

test('лента всё же прокручивается к текущей записи внутри себя', async ({ page }) => {
  // Обратная сторона: не утащить панель - мало, надо ещё и показать текущую запись.
  const app = await openApp(page);
  await app.pickTool('pencil');
  await app.setSize(6);
  for (let i = 0; i < 25; i++) await app.drag(100 + i * 20, 100, 120 + i * 20, 500);

  const st = await app.page.evaluate(() => {
    const list = document.getElementById('history-list');
    const cur = list.querySelector('.hist-item.current');
    const r = cur.parentElement.getBoundingClientRect();
    const lr = list.getBoundingClientRect();
    return { scrollTop: list.scrollTop, visible: r.top >= lr.top - 1 && r.bottom <= lr.bottom + 1 };
  });
  expect(st.visible, 'текущая запись ушла из видимой части ленты: ' + JSON.stringify(st)).toBe(true);
});

test('всё, что всплывает над холстом, имеет тёмную подложку', async ({ page }) => {
  // Стеклянные подложки приложения - это БЕЛЫЙ с прозрачностью 0.08-0.14, и рассчитаны они
  // на тёмный фон окна. Элемент, всплывающий над ХОЛСТОМ, получает под собой белое: подложка
  // становится белой, а текст на ней светлый - и пропадает начисто. Ровно это было с
  // выпадающим меню в WPF-версии (1.23.0), и ровно это нашлось здесь у подсказки, панели
  // обрезки и кнопок масштаба. У подсказки для размеров и панели текста подложка была тёмной
  // с самого начала - правило было доведено до половины.
  const app = await openApp(page);
  const bad = await app.page.evaluate(() => {
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
      if (!bg) continue;
      // Как эта подложка будет выглядеть, если под ней белый холст.
      const overWhite = {
        r: bg.r * bg.a + 255 * (1 - bg.a),
        g: bg.g * bg.a + 255 * (1 - bg.a),
        b: bg.b * bg.a + 255 * (1 - bg.a),
      };
      const fg = parse(getComputedStyle(el).color);
      if (Math.abs(lum(overWhite) - lum(fg)) < 0.4) {
        out.push({ элемент: n, фонНадБелым: +lum(overWhite).toFixed(2), текст: +lum(fg).toFixed(2) });
      }
    }
    return out;
  });
  expect(bad, 'над белым холстом подложка сливается с текстом: ' + JSON.stringify(bad)).toEqual([]);
});

test('подсказка читается: она непрозрачна настолько, чтобы не зависеть от фона', async ({ page }) => {
  const app = await openApp(page);
  const alpha = await app.page.evaluate(() => {
    const s = getComputedStyle(document.getElementById('hint')).backgroundColor;
    const m = s.match(/rgba?\(([^)]+)\)/);
    const p = m[1].split(',').map(Number);
    return p.length > 3 ? p[3] : 1;
  });
  expect(alpha, 'подсказка слишком прозрачна: ' + alpha).toBeGreaterThan(0.9);
});
