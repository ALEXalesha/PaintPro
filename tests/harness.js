// Оснастка для проверок Electron-версии.
//
// Приложение это одна страница; вся его логика живёт в глобальных функциях и в объекте
// state. Поэтому проверять его можно так же, как это делает пользователь: открыть страницу
// в браузере, поводить настоящей мышью по холсту и посмотреть на пиксели. Ничего
// подменять внутри не нужно - кроме моста в Electron, которого в браузере нет.
//
// До 1.11.2 в этой версии не было ни одной проверки. Перенос сюда правок из C#-версии без
// такой оснастки был бы игрой вслепую: файл на четыре с лишним тысячи строк, всё состояние
// общее.

const path = require('path');
const { pathToFileURL } = require('url');

const PAGE = pathToFileURL(path.join(__dirname, '..', 'paint-pro.html')).href;

/**
 * Мост в Electron, которого в браузере нет. Отдаём заглушки, чтобы страница поднялась:
 * версию берём из package.json, сохранение и диалоги просто запоминают вызовы.
 */
async function stubElectron(page) {
  const version = require('../package.json').version;
  await page.addInitScript((v) => {
    window.__calls = [];
    const log = (name) => (...args) => { window.__calls.push({ name, args }); };
    window.electronAPI = {
      getVersion: async () => v,
      pickSavePath: async () => null,
      writeImage: async () => ({ ok: true }),
      clearSavePath: async () => {},
      askUnsaved: async () => 'cancel',
      confirmClose: log('confirmClose'),
      closeAck: log('closeAck'),
      openFileDialog: async () => null,
      getFilePath: () => null,
      readDroppedFile: async () => null,
      onMenuAction: () => {},
      onOpenFile: () => {},
    };
  }, version);
}

/** Открыть приложение и дождаться, пока холст готов. */
async function openApp(page) {
  await stubElectron(page);
  await page.goto(PAGE);
  await page.waitForFunction(() => typeof state !== 'undefined' && !!document.getElementById('canvas'));
  return new App(page);
}

/**
 * Обёртка над страницей: всё, что нужно проверкам, - в терминах приложения, а не DOM.
 */
class App {
  constructor(page) { this.page = page; }

  /** Прямоугольник холста на экране: по нему координаты документа переводятся в экранные. */
  async canvasBox() {
    const box = await this.page.locator('#canvas').boundingBox();
    const size = await this.page.evaluate(() => {
      const c = document.getElementById('canvas');
      return { w: c.width, h: c.height };
    });
    return { ...box, docW: size.w, docH: size.h };
  }

  /**
   * Точка документа в экранную.
   *
   * Ровно по границе холста мышь Playwright в него не попадает: попадание считается по
   * контейнеру, и клик по углу (0, 0) уходит мимо. Сам холст такое событие принимает
   * прекрасно - проверено отправкой mousedown в тот же угол напрямую. Поэтому проверки
   * края берут отступ в пару пикселей; это ограничение оснастки, а не приложения.
   */
  async toScreen(x, y) {
    const b = await this.canvasBox();
    return { x: b.x + (x / b.docW) * b.width, y: b.y + (y / b.docH) * b.height };
  }

  /** Выбрать инструмент так же, как это делает пользователь - кнопкой на панели. */
  async pickTool(tool) {
    await this.page.click(`[data-tool="${tool}"]`);
    await this.page.waitForFunction((t) => state.tool === t, tool);
  }

  async setColor(hex) {
    await this.page.evaluate((c) => setColor(c), hex);
  }

  async setSize(px) {
    await this.page.evaluate((s) => {
      state.size = s;
      state.brushSize = s;
      syncSizeForTool && syncSizeForTool();
    }, px);
  }

  async setOpacity(value) {
    await this.page.evaluate((o) => { state.opacity = o; }, value);
  }

  /** Провести мышью из одной точки документа в другую. */
  async drag(x1, y1, x2, y2, steps = 8) {
    const a = await this.toScreen(x1, y1);
    const b = await this.toScreen(x2, y2);
    await this.page.mouse.move(a.x, a.y);
    await this.page.mouse.down();
    await this.page.mouse.move(b.x, b.y, { steps });
    await this.page.mouse.up();
    await this.settle();
  }

  /** Щёлкнуть по точке документа. */
  async clickAt(x, y) {
    const p = await this.toScreen(x, y);
    await this.page.mouse.move(p.x, p.y);
    await this.page.mouse.down();
    await this.page.mouse.up();
    await this.settle();
  }

  /** Дождаться, пока приложение закончит асинхронные дела (кадр истории грузится картинкой). */
  async settle() {
    await this.page.waitForFunction(() => !state.restoring);
    await this.page.evaluate(() => new Promise(requestAnimationFrame));
  }

  /** Цвет пикселя холста как [r, g, b, a]. */
  async pixel(x, y) {
    return this.page.evaluate(([px, py]) => {
      const ctx = document.getElementById('canvas').getContext('2d');
      return Array.from(ctx.getImageData(px, py, 1, 1).data);
    }, [x, y]);
  }

  /** Отпечаток всего холста: сравнивать снимки дешевле, чем возить пиксели наружу. */
  async fingerprint() {
    return this.page.evaluate(() => document.getElementById('canvas').toDataURL());
  }

  /** Лента истории: подписи и позиция курсора. */
  async history() {
    return this.page.evaluate(() => ({
      labels: state.history.map((h) => h.label),
      index: state.historyIndex,
      saved: state.savedHistoryIndex,
    }));
  }

  async isDirty() {
    return this.page.evaluate(() => isDirty());
  }

  async undo() { await this.page.evaluate(() => undo()); await this.settle(); }
  async redo() { await this.page.evaluate(() => redo()); await this.settle(); }

  /** Текущая строка статусбара с подсказкой, если она есть. */
  async statusText() {
    return this.page.evaluate(() => {
      const el = document.querySelector('.statusbar');
      return el ? el.innerText.replace(/\s+/g, ' ').trim() : '';
    });
  }
}

/** Белый ли пиксель (бумага). */
const isWhite = (p) => p[0] > 245 && p[1] > 245 && p[2] > 245;

/** Близок ли пиксель к заданному цвету. */
const isNear = (p, r, g, b, tol = 40) =>
  Math.abs(p[0] - r) <= tol && Math.abs(p[1] - g) <= tol && Math.abs(p[2] - b) <= tol;

module.exports = { openApp, App, isWhite, isNear, PAGE };
