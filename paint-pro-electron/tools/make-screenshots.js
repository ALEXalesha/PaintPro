// Снимки для README: собираются программой, а не руками.
//
// Скриншоты в документации гниют быстрее текста: поменяли палитру или панель слоёв - и
// картинка врёт, а заметить это некому. Поэтому они делаются тем же способом, что и
// проверки: Playwright поднимает НАСТОЯЩЕЕ приложение, рисует настоящей мышью и снимает
// окно. Перерисовать весь набор - одна команда:
//
//   npm run screenshots
//
// Снимается область renderer'а, без рамки Windows: рамка у всех своя, а внутри окна
// приложение рисует и свою строку меню, и свои кнопки, так что снимок и без неё
// читается как окно программы.
//
// Рисунок на холсте - не случайные мазки: он проходит по инструментам, которые стоит
// показать (заливка, линия, эллипс, треугольник, кисть), и потому заодно служит грубой
// проверкой, что они вообще работают.

const path = require('path');
const os = require('os');
const fs = require('fs');
const { _electron: electron } = require('playwright-core');

const APP_DIR = path.join(__dirname, '..');
const OUT_DIR = path.join(APP_DIR, 'docs', 'screenshots');

const WINDOW = { width: 1600, height: 1000 };

// Палитра из самого приложения: те же значения, что в списке colors в paint-pro.html.
const SKY = '#cfe2f3';
const GRASS = '#93c47d';
const SUN = '#ffd966';
const PEAK_LEFT = '#8e7cc3';
const PEAK_RIGHT = '#6d9eeb';
const WHITE = '#ffffff';
const INK = '#434343';

async function main() {
  fs.mkdirSync(OUT_DIR, { recursive: true });

  // Своя папка данных на запуск: у приложения замок одного экземпляра, и лежит он там.
  // С общей папкой второй запуск молча выходит - ровно как в tests-app/harness.js.
  const dataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'paintpro-shots-'));
  const app = await electron.launch({
    executablePath: require('electron'),
    args: [APP_DIR, '--user-data-dir=' + dataDir],
    cwd: APP_DIR,
  });

  // Вопрос «сохранить перед выходом?» - настоящее модальное окно. Без подмены снимки
  // сделаются, а закрыться приложение не сможет и скрипт повиснет до таймаута.
  await app.evaluate(async ({ dialog }) => {
    dialog.showMessageBox = async () => ({ response: 1 });
  });

  const win = await app.firstWindow();
  await win.waitForFunction(() => typeof state !== 'undefined' && !!document.getElementById('canvas'));

  await app.evaluate(async ({ BrowserWindow }, size) => {
    const w = BrowserWindow.getAllWindows()[0];
    w.unmaximize();
    w.setSize(size.width, size.height);
    w.center();
  }, WINDOW);
  await win.waitForTimeout(600);

  const shot = mouse(win);
  await drawLandscape(win, shot);

  // Темы. Порядок и имена - как в списке THEMES приложения.
  const themes = [
    ['glass', 'hero'],
    ['formal', null],
    ['light', null],
    ['night', null],
    ['warm', null],
  ];
  for (const [id, alias] of themes) {
    await win.evaluate((t) => setTheme(t), id);
    await win.waitForTimeout(350);
    await capture(win, `theme-${id}.png`);
    if (alias) await capture(win, `${alias}.png`);
  }

  await win.evaluate(() => setTheme('glass'));
  await win.waitForTimeout(300);

  await captureSelection(win, shot);
  await captureLayers(win, shot);

  // Снимаем признак несохранённой работы, иначе закрытие пойдёт длинным путём.
  await win.evaluate(() => {
    state.savedHistoryIndex = state.historyIndex;
    state.savedDisabledKey = disabledKey();
  });
  try { await app.close(); } catch (_) { /* окно могло закрыться само */ }
  try { fs.rmSync(dataDir, { recursive: true, force: true }); } catch (_) { /* занято - не беда */ }

  console.log(`Готово: ${OUT_DIR}`);
}

async function capture(win, name) {
  const file = path.join(OUT_DIR, name);
  await win.screenshot({ path: file });
  const kb = (fs.statSync(file).size / 1024).toFixed(0);
  console.log(`  ${name} (${kb} КБ)`);
}

/**
 * Перевод координат холста в координаты окна.
 *
 * Холст показан с масштабом, и его положение в окне зависит от размеров панелей, так
 * что считать по пикселям окна - значит переписывать скрипт после каждой правки вёрстки.
 * Все координаты ниже - в пикселях ХОЛСТА (900x600), а пересчёт живёт в одном месте.
 */
function mouse(win) {
  return {
    async to(cx, cy) {
      const box = await win.locator('#canvas').boundingBox();
      const size = await win.evaluate(() => [canvas.width, canvas.height]);
      return {
        x: box.x + (cx / size[0]) * box.width,
        y: box.y + (cy / size[1]) * box.height,
      };
    },
    async drag(x1, y1, x2, y2) {
      const a = await this.to(x1, y1);
      const b = await this.to(x2, y2);
      await win.mouse.move(a.x, a.y);
      await win.mouse.down();
      await win.mouse.move(b.x, b.y, { steps: 12 });
      await win.mouse.up();
      await win.waitForFunction(() => !state.restoring);
    },
    async click(cx, cy) {
      const p = await this.to(cx, cy);
      await win.mouse.click(p.x, p.y);
      await win.waitForFunction(() => !state.restoring);
    },
  };
}

async function use(win, tool, color, size) {
  await win.click(`[data-tool="${tool}"]`);
  // Размер ставится ТОЛЬКО через setToolSize: присваивание state.size переживает ровно до
  // следующего syncSizeForTool(), который вернёт отложенный размер инструмента обратно.
  await win.evaluate(
    ([c, s]) => {
      setColor(c);
      if (s) setToolSize(s);
    },
    [color, size || null]
  );
}

/**
 * Пейзаж на холсте 900x600.
 *
 * Фигуры рисуются контуром (fillMode по умолчанию - 'outline'), поэтому каждая закрашивается
 * вторым шагом: заливка внутрь контура тем же цветом. Так же это делал бы человек.
 */
async function drawLandscape(win, m) {
  await use(win, 'fill', SKY);
  await m.click(450, 120);

  // Горизонт: линия идёт от края до края, и толщина взята с запасом. Нажатие за пределами
  // холста приложение не считает началом мазка, поэтому концы приходится держать внутри -
  // щель в пару пикселей у края закрывают круглые торцы линии, иначе заливка земли утечёт
  // в небо и весь холст станет зелёным.
  await use(win, 'line', GRASS, 10);
  await m.drag(3, 455, 897, 455);
  await use(win, 'fill', GRASS);
  await m.click(450, 540);

  await use(win, 'ellipse', SUN, 4);
  await m.drag(330, 65, 435, 170);
  await use(win, 'fill', SUN);
  await m.click(382, 117);

  await use(win, 'triangle', PEAK_LEFT, 4);
  await m.drag(40, 462, 360, 180);
  await use(win, 'fill', PEAK_LEFT);
  await m.click(200, 400);

  await use(win, 'triangle', PEAK_RIGHT, 4);
  await m.drag(390, 462, 710, 155);
  await use(win, 'fill', PEAK_RIGHT);
  await m.click(550, 390);

  await use(win, 'brush', WHITE, 44);
  await m.drag(110, 75, 190, 75);
  await m.drag(135, 58, 170, 58);
  await m.drag(570, 60, 655, 60);
  await m.drag(595, 45, 630, 45);

  await use(win, 'brush', INK, 7);
  await m.drag(236, 40, 250, 27);
  await m.drag(250, 27, 264, 40);
  await m.drag(283, 58, 295, 48);
  await m.drag(295, 48, 307, 58);
}

/** Выделение: рамка «бегущие муравьи» и размер в строке состояния. */
async function captureSelection(win, m) {
  await win.click('[data-tool="select"]');
  await m.drag(300, 40, 470, 200);
  await win.waitForTimeout(400);
  await capture(win, 'selection.png');

  await win.evaluate(() => deselect());
  await win.waitForTimeout(250);
}

/** Панель слоёв: три слоя, у верхнего убрана непрозрачность. */
async function captureLayers(win, m) {
  await win.evaluate(() => { addLayer(); addLayer(); });
  await win.waitForTimeout(250);

  await use(win, 'brush', '#ff0000', 26);
  await m.drag(120, 520, 780, 520);

  // Ползунок двигаем вместе со значением: иначе на снимке он останется на 100%,
  // а слой будет полупрозрачным - картинка сама себе противоречит.
  await win.evaluate(() => {
    document.getElementById('layer-opacity').value = 55;
    onLayerOpacityInput(55);
  });
  await win.waitForTimeout(400);
  await capture(win, 'layers.png');
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
