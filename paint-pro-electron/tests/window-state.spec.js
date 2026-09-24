// Размер и место окна между запусками (window-state.js, 1.16.0). Модуль тот же, что в
// калькуляторах; правило - как WindowPlacement в WPF-версии. Главное - не «запомнило
// ли», а «что бы ни лежало в файле, окно откроется там, где его видно и можно взять за
// заголовок»: монитор могли отключить, разрешение уменьшить, файл обрезать.
//
// Проверки без страницы: модуль - чистые функции. Случайные прогоны - с зерном, как
// в C#-тестах: упавший прогон повторяется тем же зерном.

const { test, expect } = require('@playwright/test');
const fs = require('fs');
const os = require('os');
const path = require('path');
const WS = require('../window-state.js');

const OPTS = { width: 1400, height: 900, minWidth: 900, minHeight: 600 };
const FULL_HD = { x: 0, y: 0, width: 1920, height: 1040 };
const RIGHT = { x: 1920, y: 0, width: 2560, height: 1400 };
const inside = (w, a) => w.x >= a.x && w.y >= a.y && w.x + w.width <= a.x + a.width && w.y + w.height <= a.y + a.height;

function rng(seed) {
  let s = seed >>> 0;
  return () => {
    s = (s + 0x6D2B79F5) >>> 0;
    let t = s;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}
const int = (r, a, b) => a + Math.floor(r() * (b - a + 1));

// Экраны стоят в ряд и не перекрываются, как настоящие мониторы.
function screens(r) {
  const n = int(r, 1, 4);
  let x = int(r, -5000, 5000);
  const list = [];
  for (let i = 0; i < n; i++) {
    const a = { x, y: int(r, -3000, 3000), width: int(r, 640, 5000), height: int(r, 480, 3000) };
    list.push(a);
    x += a.width;
  }
  return list;
}

// Что угодно вместо сохранённого: мусор, дробные, огромные, отрицательные числа.
function junk(r) {
  const pick = [() => undefined, () => NaN, () => Infinity, () => 'x', () => r() * 1e5 - 5e4,
    () => int(r, -20000, 20000), () => int(r, -100, 10000)];
  const v = () => pick[int(r, 0, pick.length - 1)]();
  switch (int(r, 0, 3)) {
    case 0: return [null, 42, 'x', [], {}][int(r, 0, 4)];
    case 1: return { width: v(), height: v() };
    default: return { x: v(), y: v(), width: v(), height: v(), maximized: [true, false, 'yes', 1][int(r, 0, 3)] };
  }
}

test('без файла - размер по умолчанию, по центру', () => {
  expect(WS.restore(null, [FULL_HD], OPTS)).toEqual({ width: 1400, height: 900, maximized: false });
});

test('окно на экране открывается ровно там, где было', () => {
  const saved = { x: 100, y: 50, width: 1200, height: 800, maximized: false };
  expect(WS.restore(saved, [FULL_HD], OPTS)).toEqual(saved);
});

test('второй монитор отключили - окно по центру основного, размер тот же', () => {
  const saved = { x: 2500, y: 100, width: 1200, height: 800, maximized: false };
  expect(WS.restore(saved, [FULL_HD, RIGHT], OPTS)).toEqual(saved);
  expect(WS.restore(saved, [FULL_HD], OPTS)).toEqual({ width: 1200, height: 800, maximized: false });
});

test('меньше минимума поднимается до 900 × 600, больше экрана - до экрана', () => {
  expect(WS.restore({ width: 10, height: 10 }, [FULL_HD], OPTS)).toEqual({ width: 900, height: 600, maximized: false });
  expect(WS.restore({ x: 0, y: 0, width: 9000, height: 9000 }, [FULL_HD], OPTS))
    .toEqual({ x: 0, y: 0, width: 1920, height: 1040, maximized: false });
});

test('развёрнутое помнится только настоящим true', () => {
  expect(WS.restore({ x: 0, y: 0, width: 1000, height: 700, maximized: true }, [FULL_HD], OPTS).maximized).toBe(true);
  expect(WS.restore({ x: 0, y: 0, width: 1000, height: 700, maximized: 'yes' }, [FULL_HD], OPTS).maximized).toBe(false);
});

test('что бы ни лежало в файле, размер разумный, заголовок на экране, повтор ничего не меняет', () => {
  for (let seed = 1; seed <= 3000; seed++) {
    const r = rng(seed);
    const sc = screens(r);
    const w = WS.restore(junk(r), sc, OPTS);
    const ctx = 'зерно ' + seed;
    expect(Number.isInteger(w.width) && Number.isInteger(w.height), ctx).toBe(true);
    expect(w.width >= OPTS.minWidth && w.height >= OPTS.minHeight, ctx).toBe(true);
    expect(typeof w.maximized, ctx).toBe('boolean');
    expect(w.x === undefined, ctx).toBe(w.y === undefined);
    if (w.x !== undefined) {
      expect(sc.some((a) => inside(w, a) || w.width > a.width || w.height > a.height), ctx).toBe(true);
      expect(sc.some((a) => w.x >= a.x && w.y >= a.y && w.x < a.x + a.width
        && w.y + WS.GRIP_HEIGHT <= a.y + a.height), ctx).toBe(true);
      expect(WS.restore(w, sc, OPTS), ctx).toEqual(w);
    }
  }
});

test('окно, которое помещалось на своём экране, возвращается без изменений', () => {
  let checked = 0;
  for (let seed = 1; seed <= 2000; seed++) {
    const r = rng(seed);
    const a = screens(r)[0];
    const width = int(r, 900, Math.max(900, a.width)), height = int(r, 600, Math.max(600, a.height));
    if (width > a.width || height > a.height) continue;
    const saved = { x: a.x + int(r, 0, a.width - width), y: a.y + int(r, 0, a.height - height),
                    width, height, maximized: r() < 0.5 };
    expect(WS.restore(saved, [a], OPTS), 'зерно ' + seed).toEqual(saved);
    checked++;
  }
  expect(checked).toBeGreaterThan(500);
});

test('запись и чтение файла; обрезанный файл читается как «ничего»', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'ws-'));
  try {
    const file = path.join(dir, 'nested', 'window-state.json');
    const st = { x: 10, y: 20, width: 1000, height: 700, maximized: true };
    expect(WS.save(file, st)).toBe(true);
    expect(WS.load(file)).toEqual(st);
    expect(fs.existsSync(file + '.tmp')).toBe(false);
    for (const text of ['', '{"x": 10, "wid', 'null']) {
      fs.writeFileSync(file, text);
      expect(WS.restore(WS.load(file), [FULL_HD], OPTS), text).toEqual({ width: 1400, height: 900, maximized: false });
    }
    expect(WS.save(dir, { width: 1, height: 1 })).toBe(false); // вместо файла каталог - не падает
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('у развёрнутого окна запоминаются обычные границы', () => {
  const win = {
    getNormalBounds: () => ({ x: 5, y: 6, width: 1000, height: 700 }),
    getBounds: () => ({ x: 0, y: 0, width: 1920, height: 1040 }),
    isMaximized: () => true,
  };
  expect(WS.capture(win)).toEqual({ x: 5, y: 6, width: 1000, height: 700, maximized: true });
});

test('модуль тот же, что в калькуляторах, кроме комментариев сверху', () => {
  const calc = path.join(__dirname, '..', '..', '..', 'Calculators', 'calcpro-glass', 'window-state.js');
  test.skip(!fs.existsSync(calc), 'калькуляторов рядом нет (сборка на CI)');
  const body = (f) => fs.readFileSync(f, 'utf8').replace(/\r\n/g, '\n').split('const finite')[1];
  expect(body(path.join(__dirname, '..', 'window-state.js'))).toBe(body(calc));
});
