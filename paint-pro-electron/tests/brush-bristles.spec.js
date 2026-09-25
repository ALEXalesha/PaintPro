// Кисть с ворсинками (1.19.0). До неё кисть рисовала ровно то же, что карандаш.
//
// Пучок ворсинок задаёт зерно, оно пишется в запись мазка. Генератор и таблица те же, что
// в C#-версии (Services/Bristles.cs): числа ниже посчитаны здесь и стоят в BristleTests.cs.

const { test, expect } = require('@playwright/test');
const { openApp } = require('./harness');

const LINE = [[60, 300], [300, 300], [560, 300], [840, 300]];

/** Мазок инструментом по точкам документа, с заданным зерном кисти. */
async function stroke(app, tool, size, { seed = 42, color = '#c0392b', points = LINE } = {}) {
  await app.pickTool(tool);
  await app.setColor(color);
  await app.setSize(size);
  await app.page.evaluate((s) => { state.nextBrushSeed = s; }, seed);
  const a = await app.toScreen(points[0][0], points[0][1]);
  await app.page.mouse.move(a.x, a.y);
  await app.page.mouse.down();
  for (const [x, y] of points.slice(1)) {
    const p = await app.toScreen(x, y);
    await app.page.mouse.move(p.x, p.y, { steps: 12 });
  }
  await app.page.mouse.up();
  await app.settle();
}

/** Столбец пикселей холста [r, g, b, a] в точке x, от y0 до y1. */
async function column(app, x, y0, y1) {
  return app.page.evaluate(([cx, a, b]) => {
    const d = document.getElementById('canvas').getContext('2d').getImageData(cx, a, 1, b - a + 1).data;
    const out = [];
    for (let i = 0; i < d.length; i += 4) out.push([d[i], d[i + 1], d[i + 2], d[i + 3]]);
    return out;
  }, [x, y0, y1]);
}

const isInk = ([r, g, b]) => r + g + b < 600;

// ─────────── Генератор и пучок: те же числа, что в C# ───────────

test('генератор - mulberry32, те же числа, что в C#', async ({ page }) => {
  await openApp(page);
  const v = await page.evaluate(() => [0, 1, 42, 4294967295].map((s) => {
    const n = mulberry32(s);
    return [n(), n(), n()];
  }));
  const want = [
    [0.266429208685, 0.000329745701, 0.223272027448],
    [0.627073940588, 0.002735721180, 0.527447039960],
    [0.601103751920, 0.448290558998, 0.852465793490],
    [0.896422614111, 0.189478256740, 0.715652678162],
  ];
  v.forEach((row, i) => row.forEach((x, j) => expect(x).toBeCloseTo(want[i][j], 11)));
});

test('пучок с тем же зерном - тот же, что в C#', async ({ page }) => {
  await openApp(page);
  const want = [
    [42, 1, -0.32046555678701766, -0.2723769783989844, 0.09688138290075586, 0.0664292815234512],
    [42, 31, -0.415253010181137, -0.11452843497612411, 0.11131463490659371, 0.34510250213555993],
    [7, 7, 0.1265133776974229, 0.39528951115146016, 0.1033311586547643, 0.028316874243319034],
    [3000000000, 1, 0.05688875296562796, -0.4140510997587827, 0.07347213685512544, 0.1663915789918974],
  ];
  for (const [seed, i, dx, dy, w, shade] of want) {
    const b = await page.evaluate(([s, k]) => bristlesFor(s)[k], [seed, i]);
    expect(b.dx).toBeCloseTo(dx, 12);
    expect(b.dy).toBeCloseTo(dy, 12);
    expect(b.w).toBeCloseTo(w, 12);
    expect(b.shade).toBeCloseTo(shade, 12);
  }
});

test('ворсинка 0 - сердцевина; пучок внутри круга размера и почти до края в любую сторону; оттенки только к белому', async ({ page }) => {
  await openApp(page);
  const bad = await page.evaluate(() => {
    const out = [];
    for (let s = 0; s < 2000; s++) {
      const set = bristlesFor(Math.imul(s, 2654435761) >>> 0);
      if (set.length !== BRISTLE_COUNT) out.push(['count', s]);
      const c = set[0];
      if (c.dx !== 0 || c.dy !== 0 || c.w !== BRISTLE_CORE_WIDTH || c.shade !== 0) out.push(['core', s]);
      for (const b of set) {
        if (Math.hypot(b.dx, b.dy) + b.w / 2 > 0.5 + 1e-12) out.push(['reach', s]);
        if (b.shade < 0 || b.shade > BRISTLE_SHADE_MIN + BRISTLE_SHADE_SPREAD) out.push(['shade', s]);
      }
      // в любом направлении пучок достаёт почти до края: полоса не уже 0.84 размера
      for (let d = 0; d < 180; d += 6) {
        const th = d * Math.PI / 180;
        let up = 0, down = 0;
        for (const b of set) {
          const perp = -b.dx * Math.sin(th) + b.dy * Math.cos(th);
          up = Math.max(up, perp + b.w / 2);
          down = Math.max(down, -perp + b.w / 2);
        }
        if (Math.min(up, down) < 0.42) out.push(['narrow', s, d]);
      }
    }
    return out;
  });
  expect(bad).toEqual([]);
});

// ─────────── Мазок ───────────

test('кисть больше не рисует то же, что карандаш', async ({ page }) => {
  const app = await openApp(page);
  await stroke(app, 'brush', 60);
  const brush = await column(app, 450, 260, 340);
  const app2 = await openApp(await page.context().newPage());
  await stroke(app2, 'pencil', 60);
  const pencil = await column(app2, 450, 260, 340);
  let diff = 0;
  brush.forEach((p, i) => { if (p.join() !== pencil[i].join()) diff++; });
  expect(diff).toBeGreaterThan(10);
});

test('середина мазка - ровно выбранный цвет, а поперёк есть полоски светлее', async ({ page }) => {
  const app = await openApp(page);
  await stroke(app, 'brush', 60, { color: '#202060' });
  const col = await column(app, 450, 268, 332);
  expect(col[32]).toEqual([0x20, 0x20, 0x60, 255]);
  const shades = new Set(col.filter(isInk).map((p) => p.join()));
  expect(shades.size).toBeGreaterThanOrEqual(4);
});

test('за кругом размера кисть не рисует', async ({ page }) => {
  const app = await openApp(page);
  await stroke(app, 'brush', 60, { seed: 3 });
  const outside = await page.evaluate(() => {
    const d = document.getElementById('canvas').getContext('2d').getImageData(0, 0, 900, 600).data;
    let n = 0;
    for (let y = 0; y < 600; y++) for (let x = 0; x < 900; x++) {
      const i = (y * 900 + x) * 4;
      if (d[i] === 255 && d[i + 1] === 255 && d[i + 2] === 255) continue;
      const cx = Math.min(840, Math.max(60, x + 0.5));
      if (Math.hypot(x + 0.5 - cx, y + 0.5 - 300) > 31.5) n++;
    }
    return n;
  });
  expect(outside).toBe(0);
});

test('тонкая кисть - сплошная линия, как карандаш', async ({ page }) => {
  const app = await openApp(page);
  await stroke(app, 'brush', 5);
  const brush = await app.fingerprint();
  const app2 = await openApp(await page.context().newPage());
  await stroke(app2, 'pencil', 5);
  expect(await app2.fingerprint()).toBe(brush);
});

test('зерно записано в мазок, у каждого мазка своё', async ({ page }) => {
  const app = await openApp(page);
  await page.evaluate(() => { state.nextBrushSeed = null; });
  await app.pickTool('brush');
  await app.setSize(30);
  for (let i = 0; i < 6; i++) await app.drag(100, 100 + i * 60, 700, 100 + i * 60);
  const seeds = await page.evaluate(() => state.history.slice(1).map((h) => h.replay && h.replay.seed));
  expect(seeds.every((s) => Number.isInteger(s) && s >= 0 && s < 2 ** 32)).toBe(true);
  expect(new Set(seeds).size).toBeGreaterThanOrEqual(5);
});

test('пересборка ленты рисует тот же пучок: холст до пикселя прежний', async ({ page }) => {
  const app = await openApp(page);
  await stroke(app, 'pencil', 20, { color: '#00aa00', points: [[100, 100], [800, 500]] });
  await stroke(app, 'brush', 70, { seed: 1234, points: [[80, 300], [300, 250], [600, 360], [850, 300]] });
  await stroke(app, 'brush', 40, { seed: 99, color: '#2e86de', points: [[400, 50], [420, 550]] });
  const before = await app.fingerprint();
  await page.evaluate(() => setEntryEnabled(1, false));
  await app.settle();
  expect(await app.fingerprint()).not.toBe(before);
  await page.evaluate(() => setEntryEnabled(1, true));
  await app.settle();
  expect(await app.fingerprint()).toBe(before);
});

test('колесо посреди мазка: пучок растёт вместе с размером', async ({ page }) => {
  const app = await openApp(page);
  await app.pickTool('brush');
  await app.setColor('#000000');
  await app.setSize(20);
  await page.evaluate(() => { state.nextBrushSeed = 42; });
  const a = await app.toScreen(60, 300);
  const m = await app.toScreen(300, 300);
  const b = await app.toScreen(840, 300);
  await page.mouse.move(a.x, a.y);
  await page.mouse.down();
  await page.mouse.move(m.x, m.y, { steps: 10 });
  await app.setSize(80);
  await page.mouse.move(b.x, b.y, { steps: 10 });
  await page.mouse.up();
  await app.settle();
  const thin = (await column(app, 180, 200, 400)).filter(isInk).length;
  const wide = (await column(app, 700, 200, 400)).filter(isInk).length;
  expect(thin).toBeLessThanOrEqual(21);
  expect(wide).toBeGreaterThan(40);
});

test('запись кисти без зерна (до 1.19.0) повторяется сплошной линией, как была нарисована', async ({ page }) => {
  const app = await openApp(page);
  await stroke(app, 'pencil', 30, { color: '#aa00aa' });
  const pencil = await app.fingerprint();
  // Та же запись, но как старая кисть: инструмент brush, зерна нет.
  await page.evaluate(() => {
    const h = state.history[state.history.length - 1];
    h.replay.tool = 'brush';
    delete h.replay.seed;
  });
  await page.evaluate(() => setEntryEnabled(1, false));
  await app.settle();
  await page.evaluate(() => setEntryEnabled(1, true));
  await app.settle();
  expect(await app.fingerprint()).toBe(pencil);
});

test('порядок ворсинок ни на что не влияет: пунктира вдоль ворсинок нет', async ({ page }) => {
  await openApp(page);
  const worst = await page.evaluate(() => {
    function paint(set) {
      const c = document.createElement('canvas');
      c.width = 300; c.height = 160;
      const g = c.getContext('2d');
      g.strokeStyle = '#c0392b';
      g.lineCap = 'round';
      g.lineJoin = 'round';
      const pts = [[30, 80], [90, 70], [160, 92], [260, 78]];
      strokeDot(g, 'brush', set, 30, 80, 70);
      for (let i = 1; i < pts.length; i++) strokeSegment(g, 'brush', set, ...pts[i - 1], ...pts[i], 70);
      return g.getImageData(0, 0, 300, 160).data;
    }
    const a = paint(bristlesFor(42));
    const b = paint(bristlesFor(42).reverse());
    let w = 0;
    for (let i = 0; i < a.length; i += 4) {
      if (a[i + 3] < 200 && b[i + 3] < 200) continue;
      w = Math.max(w, Math.abs(a[i] - b[i]), Math.abs(a[i + 1] - b[i + 1]), Math.abs(a[i + 2] - b[i + 2]));
    }
    return w;
  });
  expect(worst).toBeLessThanOrEqual(20);
});

test('карандаш, маркер и ластик ворсинок не получили', async ({ page }) => {
  const app = await openApp(page);
  await stroke(app, 'pencil', 40, { color: '#000000' });
  const col = await column(app, 450, 282, 318);
  expect(col.every((p) => p[0] === 0 && p[1] === 0 && p[2] === 0)).toBe(true);
  expect(await page.evaluate(() => state.history[state.history.length - 1].replay.seed)).toBeNull();
});
