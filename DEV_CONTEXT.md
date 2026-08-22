# Paint Pro — Dev Context (для переноса стиля и подходов в другие проекты, например калькулятор)

> Цель документа: дать модели в новой сессии всё необходимое, чтобы воссоздать визуальный язык, стек, паттерны UI/UX и архитектуру Paint Pro в другом приложении (например, калькулятор) без переоткрытия дискуссий.

---

## 1. Стек и сборка

- **Платформа:** Electron 33.4.11 + Node 24.14 / npm 11.9.
- **electron-builder 25.1.8** — Windows portable + NSIS installer.
- **Один HTML-файл (`paint-pro.html` ≈ 3000+ строк)** содержит CSS + HTML + ванильный JS. Никаких бандлеров, фреймворков, transpile-шага. Это намеренно — приложение «open-and-edit».
- **`main.js`** — создаёт `BrowserWindow`, отключает нативное меню (`Menu.setApplicationMenu(null)` + `setMenuBarVisibility(false)`), обрабатывает `ipcMain.handle('pick-save-path' | 'write-image' | 'clear-save-path' | 'open-file-dialog' | 'read-dropped-file')` и single-instance lock + ассоциации файлов. Сохранение разведено на два вызова специально: renderer должен знать расширение до `toDataURL`, иначе JPEG уезжает в файл PNG-байтами. `clear-save-path` обнуляет `global.lastSavedPath`: без него «Файл → Новый» оставлял документ привязанным к прежней картинке, и Ctrl+S перезаписывал её чистым холстом.
- **`preload.js`** — `contextBridge.exposeInMainWorld('electronAPI', { pickSavePath, writeImage, clearSavePath, openFileDialog, getFilePath, readDroppedFile, onMenuAction, onOpenFile })`. `contextIsolation:true`, `nodeIntegration:false`.
- **`package.json` build-конфиг:** `target: ['portable', 'nsis']`, иконки в `build/icon.ico` + `build/icon.png`. NSIS — `oneClick:false, perMachine:false, allowToChangeInstallationDirectory:true`.

Команды:
```
npm run build           # PaintPro-<версия>-portable.exe
npm run build-installer # Paint Pro Setup <версия>.exe
```

---

## 2. Визуальный язык — Apple Liquid Glass (iOS 26 / macOS Tahoe 26)

Главная идея: **полупрозрачные стеклянные панели поверх насыщенного цветного фона, с плавающими «пузырями» и тонкими бликами**. Цвета подсасываются `backdrop-filter: blur(...) saturate(...)` через стекло.

### 2.1 CSS-токены (must-have)

```css
:root {
  --glass-bg:          rgba(255,255,255,0.08);
  --glass-bg-strong:   rgba(255,255,255,0.14);
  --glass-bg-soft:     rgba(255,255,255,0.05);
  --glass-border:      rgba(255,255,255,0.18);
  --glass-border-strong:rgba(255,255,255,0.32);
  --glass-highlight:   rgba(255,255,255,0.45);
  --glass-shadow:      0 8px 32px rgba(0,0,0,0.35);
  --glass-inset:       inset 0 1px 0 rgba(255,255,255,0.35),
                       inset 0 -1px 0 rgba(0,0,0,0.15);
  --blur:              blur(28px) saturate(190%);
  --blur-strong:       blur(40px) saturate(220%);

  --text:              #f4f4f8;
  --text-dim:          rgba(244,244,248,0.62);

  --accent:            #5b8def;
  --accent-2:          #9d5bef;
  --accent-grad:       linear-gradient(135deg,#5b8def 0%, #9d5bef 100%);
  --accent-glow:       0 0 24px rgba(91,141,239,0.55);
}
```

### 2.2 Фон-«обои» на body — обязательно цветной

```css
body {
  background:
    radial-gradient(1100px 700px at 12% -10%, rgba(120,90,255,0.55), transparent 60%),
    radial-gradient(900px 600px at 95% 8%,    rgba(255,100,180,0.40), transparent 55%),
    radial-gradient(800px 800px at 85% 110%,  rgba(60,200,255,0.40),  transparent 60%),
    radial-gradient(700px 600px at 5% 105%,   rgba(100,255,180,0.30), transparent 60%),
    linear-gradient(135deg,#0e0c1a 0%,#1a1330 50%,#0c1024 100%);
  background-attachment: fixed;
}
```

Почему: без цветного фона `backdrop-filter: blur+saturate` нечего размывать, стекло вырождается в серый прямоугольник.

### 2.3 Стандартная стеклянная панель

```css
.panel {
  background: var(--glass-bg);
  backdrop-filter: var(--blur);
  -webkit-backdrop-filter: var(--blur);
  border: 1px solid var(--glass-border);
  border-radius: 18-22px;     /* большие радиусы */
  box-shadow: var(--glass-shadow), var(--glass-inset);
  position: relative;
  /* overflow:visible — НЕ ставить overflow:hidden, сломает тултипы и выпадайки */
}
```

### 2.4 Тонкий блик сверху (specular)

```css
.panel::before {
  content: '';
  position: absolute; inset: 0; border-radius: inherit;
  background: linear-gradient(180deg, rgba(255,255,255,0.10) 0%, rgba(255,255,255,0.02) 35%, transparent 60%);
  pointer-events: none; z-index: 0;
}
.panel > * { position: relative; z-index: 1; }   /* контент поверх блика */
```

### 2.5 Бегущий блик (shimmer) — только на крупных стационарных панелях

```css
.panel-with-shimmer::after {
  content: ''; position: absolute;
  top: 0; left: -50%; width: 50%; height: 100%;
  background: linear-gradient(110deg,
    transparent 0%,
    rgba(255,255,255,0.10) 45%,
    rgba(255,255,255,0.20) 50%,
    rgba(255,255,255,0.10) 55%,
    transparent 100%);
  pointer-events: none; border-radius: inherit;
  animation: shimmer 12s linear infinite;
  z-index: 0;
}
@keyframes shimmer { to { transform: translateX(400%); } }
```

⚠️ **Не ставить shimmer на панель, у которой есть child-тултипы (data-tip ::after).** Shimmer требует `overflow:hidden`, который обрежет тултипы, выходящие за границы панели. Решение: либо опустить shimmer, либо клипать только сам shimmer-элемент через child-wrapper с overflow:hidden.

### 2.6 Анимированные фоновые пузыри

HTML:
```html
<div class="bubbles" aria-hidden="true">
  <span class="bubble violet" style="left:6%;  width:140px; height:140px; --dur:26s; --delay:0s;  --drift:60px"></span>
  <span class="bubble pink"   style="left:18%; width:90px;  height:90px;  --dur:22s; --delay:-6s; --drift:-40px"></span>
  ...10–12 штук
</div>
```

CSS:
```css
.bubbles { position: fixed; inset: 0; overflow: hidden; pointer-events: none; z-index: 0; }
.bubble {
  position: absolute; bottom: -200px; border-radius: 50%;
  background: radial-gradient(circle at 30% 30%,
    rgba(255,255,255,0.55) 0%,
    rgba(180,200,255,0.25) 35%,
    rgba(120,90,255,0.10)  70%,
    transparent 100%);
  box-shadow:
    inset -8px -8px 18px rgba(255,255,255,0.18),
    inset  6px  6px 18px rgba(255,255,255,0.35),
    0 8px 30px rgba(120,90,255,0.25);
  backdrop-filter: blur(2px);
  opacity: 0;
  animation: bubbleRise var(--dur,24s) ease-in-out infinite;
  animation-delay: var(--delay,0s);
  will-change: transform, opacity;
}
.bubble::after {  /* блик-glare */
  content: ''; position: absolute; top: 12%; left: 18%;
  width: 28%; height: 22%; border-radius: 50%;
  background: radial-gradient(circle, rgba(255,255,255,0.85) 0%, transparent 70%);
  filter: blur(1.5px);
}
@keyframes bubbleRise {
  0%   { transform: translate(0, 0) scale(0.85); opacity: 0; }
  8%   { opacity: 0.85; }
  50%  { transform: translate(var(--drift,30px), -55vh) scale(1); opacity: 0.95; }
  92%  { opacity: 0.6; }
  100% { transform: translate(calc(var(--drift,30px) * -0.6), -120vh) scale(1.08); opacity: 0; }
}
.bubble.pink   { background: radial-gradient(circle at 30% 30%, rgba(255,255,255,0.55), rgba(255,170,210,0.28) 40%, rgba(255,100,180,0.12) 75%, transparent); }
.bubble.cyan   { ... }   /* варианты тонов: pink/cyan/green/violet/gold */
```

⚠️ **Контент-слои ставим z-index:2** (`.topbar, .toolbar, .main, .statusbar, .zoom-controls { position:relative; z-index:2 }`), чтобы пузыри (z-index:0) были под ними, но видны через стекло.

### 2.7 Кнопки

Базовая «glass-pill»:
```css
.btn {
  background: var(--glass-bg-soft);
  border: 1px solid var(--glass-border);
  color: var(--text);
  padding: 8px 14px;
  border-radius: 12px;
  font-weight: 500;
  transition: transform .18s ease, background .18s, border-color .18s, box-shadow .18s;
  backdrop-filter: blur(14px);
}
.btn:hover  { background: var(--glass-bg-strong); border-color: var(--glass-border-strong); transform: translateY(-1px); }
.btn:active { transform: translateY(0); }
.btn.primary {
  background: var(--accent-grad);
  border-color: rgba(255,255,255,0.25);
  color: white;
  box-shadow: var(--accent-glow), inset 0 1px 0 rgba(255,255,255,0.4);
}
```

Активная кнопка (selected tool / pressed): тот же градиент primary + accent-glow.

### 2.8 Ripple на клик

```css
.ripple {
  position: absolute; border-radius: 50%; pointer-events: none;
  background: radial-gradient(circle,
    rgba(255,255,255,0.55) 0%,
    rgba(255,255,255,0.25) 35%,
    rgba(120,180,255,0.10) 70%,
    transparent 100%);
  box-shadow: inset 0 0 12px rgba(255,255,255,0.4), 0 0 18px rgba(91,141,239,0.4);
  transform: translate(-50%,-50%) scale(0);
  animation: rippleGrow .65s ease-out forwards;
  z-index: 5;
}
@keyframes rippleGrow {
  0%   { transform: translate(-50%,-50%) scale(0);   opacity: 0.9; }
  100% { transform: translate(-50%,-50%) scale(2.6); opacity: 0; }
}
```

JS:
```js
function spawnRipple(target, clientX, clientY) {
  const rect = target.getBoundingClientRect();
  const size = Math.max(rect.width, rect.height) * 1.4;
  const r = document.createElement('span');
  r.className = 'ripple';
  r.style.cssText = `width:${size}px; height:${size}px;
    left:${clientX-rect.left}px; top:${clientY-rect.top}px`;
  if (getComputedStyle(target).position === 'static') target.style.position = 'relative';
  target.appendChild(r);
  setTimeout(() => r.remove(), 700);
  // ⚠️ НЕ ставим overflow:hidden на target — обрезает тултипы.
  // Мягкий radial-fade ripple допустимо чуть выходит за края (выглядит как glow).
}
document.addEventListener('mousedown', (e) => {
  if (e.button !== 0) return;
  const t = e.target.closest('.btn, .tool, .menu-btn /* и т.д. */');
  if (t) spawnRipple(t, e.clientX, e.clientY);
});
```

### 2.9 Скроллбар

```css
::-webkit-scrollbar { width: 10px; height: 10px; }
::-webkit-scrollbar-track { background: transparent; }
::-webkit-scrollbar-thumb {
  background: rgba(255,255,255,0.18);
  border-radius: 999px;
  border: 2px solid transparent;
  background-clip: padding-box;
}
::-webkit-scrollbar-thumb:hover { background: rgba(255,255,255,0.32); background-clip: padding-box; }
```

### 2.10 Range-slider (для глассового стиля)

```css
input[type="range"] {
  -webkit-appearance: none; appearance: none;
  width: 110px; height: 6px;
  background: rgba(255,255,255,0.12);
  border-radius: 999px; outline: none;
  border: 1px solid var(--glass-border);
}
input[type="range"]::-webkit-slider-thumb {
  -webkit-appearance: none; width: 16px; height: 16px;
  border-radius: 50%;
  background: var(--accent-grad);
  border: 1px solid rgba(255,255,255,0.5);
  box-shadow: 0 2px 8px rgba(0,0,0,0.4), inset 0 1px 0 rgba(255,255,255,0.5);
}
```

### 2.11 Тултипы

```css
[data-tip]:hover::after {
  content: attr(data-tip);
  position: absolute; top: calc(100% + 8px); left: 50%;
  transform: translateX(-50%);
  background: rgba(20,20,30,0.85);
  backdrop-filter: blur(20px);
  border: 1px solid var(--glass-border);
  color: var(--text);
  padding: 6px 10px; border-radius: 8px;
  font-size: 11px; font-weight: 500;
  white-space: nowrap; pointer-events: none;
  z-index: 100;
  box-shadow: 0 6px 18px rgba(0,0,0,0.4);
}
```

⚠️ Родитель тултипа НЕ должен быть `overflow: hidden`.

### 2.12 Шрифт и типографика

```css
font-family: -apple-system, BlinkMacSystemFont, "SF Pro Display", "Segoe UI", Roboto, sans-serif;
font-variant-numeric: tabular-nums;   /* для всех числовых значений */
letter-spacing: 0.2px;                /* в логотипе/заголовках */
```

---

## 3. Архитектура (для калькулятора)

### 3.1 Структура layout

```
body (flex column, 100vh)
├── .bubbles                  (z-index 0, position fixed, inset 0)
├── .topbar / .menubar        (опционально, для приложения с окном)
├── .toolbar                  (опционально)
├── .main (flex 1)
│   ├── .display-area / .canvas-area     (стеклянная панель)
│   └── .sidebar              (правая панель, стеклянная)
└── .statusbar                (опционально)
```

Каждая панель — `margin: 6px-10px`, `border-radius: 18-22px`. Между панелями — узкие зазоры (10px), сквозь которые видны пузыри.

### 3.2 Состояние

Один глобальный объект `state` (literal). Никаких классов/Redux:

```js
let state = {
  // ... чисто данные приложения
};
```

Все мутации — прямой `state.x = y`. UI обновляется явными вызовами `update*()`.

### 3.3 Хоткеи

```js
document.addEventListener('keydown', (e) => {
  if (e.target.tagName === 'INPUT' || e.target.isContentEditable) return;
  if (e.ctrlKey || e.metaKey) {
    if (e.key === 'z') { e.preventDefault(); undo(); }
    // ...
  } else {
    const map = { v:'cursor', p:'pencil', /* ... */ };
    if (map[e.key.toLowerCase()]) selectTool(map[e.key.toLowerCase()]);
  }
});
```

### 3.4 История (undo/redo) — паттерн из Paint Pro

```js
state.history = [];
state.historyIndex = -1;
function saveHistory() {
  state.history = state.history.slice(0, state.historyIndex + 1);
  state.history.push(serializeState());
  state.historyIndex = state.history.length - 1;
}
function undo() { if (state.historyIndex > 0) { state.historyIndex--; restoreHistory(); } }
function redo() { if (state.historyIndex < state.history.length-1) { state.historyIndex++; restoreHistory(); } }
```

Для калькулятора: `serializeState` = `{display, expression, memory}` JSON; `restoreHistory` присваивает обратно и обновляет UI.

---

## 4. Калькулятор в стиле Paint Pro — конкретный план

### 4.1 Структура

```
body
├── .bubbles                   (фоновые пузыри)
├── .calc-shell .panel         (стеклянная панель ~ 380×600 в центре)
│   ├── .calc-header           (заголовок + переключатель режимов: Standard / Scientific / Programmer)
│   ├── .calc-display .panel   (вложенная панель: история сверху мелко, основной результат снизу крупно)
│   ├── .calc-mem-row          (M+, M-, MR, MC — узкая строка small glass pills)
│   └── .calc-keys             (grid 4–5 колонок)
│       ├── digit btn
│       ├── op btn (более яркий, primary градиент на = )
│       └── func btn (sin, cos, log, %)
└── .statusbar (опц., например показывает «Rad/Deg», ans, копирование)
```

### 4.2 Стилевые решения

- **Главная панель калькулятора:** glass + большие радиусы 22-26px, тень `0 20px 60px rgba(0,0,0,0.5)`.
- **Дисплей:** вложенная панель (`var(--glass-bg-soft)`), внутри `font-variant-numeric: tabular-nums`, `font-size: 44px` для результата, `18px` для истории/выражения сверху, `text-align: right`.
- **Цифровые кнопки:** `.btn` (см. 2.7), без primary.
- **Операции (+, −, ×, ÷):** `.btn` с лёгким акцентом (`background: rgba(91,141,239,0.18)`).
- **Кнопка `=`:** `.btn.primary` с accent-grad + accent-glow.
- **AC / C / ⌫:** `background: rgba(255,91,91,0.18); border-color: rgba(255,120,120,0.4); color:#ffb3b3`.
- **Сетка кнопок:** `display:grid; grid-template-columns: repeat(4,1fr); gap: 10px; padding: 14px`.
- **Hover:** `transform: translateY(-1px)` + усиление background.
- **Active (mousedown):** `transform: translateY(0) scale(0.97)`.
- **Ripple** на каждое нажатие (см. 2.8).

### 4.3 Состояние и логика

```js
let state = {
  expression: '',     // текущее выражение
  display: '0',       // что показано
  result: null,       // последний результат (для chain)
  memory: 0,
  mode: 'standard',   // standard | scientific | programmer
  angleMode: 'deg',   // для тригонометрии
  history: [],        // массив строк "выражение = результат"
  justEvaluated: false
};
```

Парсинг: **ТОЛЬКО собственный recursive-descent парсер, никогда `eval()`** — eval откроет XSS если поделиться скрином/ссылкой.

```js
// Pratt parser или simple shunting-yard.
// Лексер: number | op | paren | func.
// Поддержка: + - * / % ^ ( ) и функции sqrt sin cos tan log ln abs.
function evaluate(expr) {
  const tokens = tokenize(expr);
  const ast = parse(tokens);
  return evalAst(ast);
}
```

Округление: `Number(value.toPrecision(12))` — убирает мусор `0.1+0.2=0.30000…04`.

### 4.4 Хоткеи для калькулятора

```js
const KEY_MAP = {
  '0':'0','1':'1', /* ... */ '9':'9',
  '.':'.', ',':'.',
  '+':'+', '-':'-', '*':'*', '/':'/',
  'Enter':'=', '=':'=',
  'Backspace':'⌫', 'Delete':'C',
  'Escape':'AC',
  '(':'(', ')':')',
  '%':'%', '^':'^'
};
document.addEventListener('keydown', e => {
  const k = KEY_MAP[e.key];
  if (k) { e.preventDefault(); pressKey(k); }
  if (e.ctrlKey && e.key === 'c') copyResult();
});
```

### 4.5 Анимация вывода результата

При `=`:
1. Старое выражение сдвигается вверх (в строку истории) с `transition: transform 0.3s, opacity 0.3s`.
2. Новый результат появляется снизу с `transform: translateY(20px); opacity:0` → `0,1` (`.25s ease-out`).
3. На дисплее короткий «pop-glow»: `box-shadow` на 0.3s становится `0 0 30px rgba(91,141,239,0.5)`.

### 4.6 Полезные UX-детали Paint Pro, переносимые в калькулятор

| Из Paint Pro | В калькулятор |
|---|---|
| Custom menubar (Файл/Правка/Вид) | Меню: View → mode (Standard/Scientific), Edit → Copy result / Paste, History panel toggle |
| Sidebar с настройками | Sidebar с историей вычислений (список последних 50, click — вставить в выражение) |
| Тултипы на тулзах | Тултипы на функциональных кнопках (sin → «sin(x), Rad/Deg в зависимости от режима») |
| Хоткеи V/P/B/M/E/G/I/T | Хоткеи Ctrl+H — toggle history, Ctrl+M — memory store/recall и т.д. |
| Размер кисти / ластика раздельно (через slider) | — (н/а) |
| Undo/Redo (Ctrl+Z/Y) | Undo/Redo для последовательности нажатий: «удалил → undo → вернулась цифра» |
| Сохранение PNG | Export history → .txt / .csv |
| Электрон-IPC + drag&drop | DnD числа / выражения из буфера, paste из буфера обмена с очисткой нечисловых символов |
| Окно без нативного меню (`Menu.setApplicationMenu(null)`) | Идентично — у нас своё меню в renderer |

### 4.7 Файлы для калькулятора

```
calc-pro-electron/
├── main.js                # копия: Menu.setApplicationMenu(null), single-instance, IPC для save/open
├── preload.js             # тот же contextBridge
├── calc-pro.html          # CSS + HTML + JS в одном файле
├── package.json           # build: portable + nsis
└── build/icon.ico, icon.png
```

---

## 5. Антипаттерны, на которые я уже наступал в Paint Pro (не повторять)

1. **`overflow: hidden` на стеклянной панели c child-тултипами** → тултипы обрезаются.
   Решение: либо overflow:visible, либо обернуть shimmer-эффект в child-wrapper.

2. **`document.addEventListener('mousedown', commitText)` без проверки `e.target === canvas`** → канвас триггерит mousedown, обработчик сразу закрывает только что открытый редактор.
   Решение: добавлять `e.target !== canvas` или вводить флаг `justOpened`.

3. **Курсор-пузырь, следующий за мышкой** → выглядит как баг отрисовки, отвлекает.
   Решение: только ripple на клике, без постоянной линзы.

4. **Inline `style="background:var(--bg-3)"` на кнопках** при смене темы — нужно либо удалять inline, либо перебивать через `.parent button { ... !important }`. Я выбрал второе для быстрого фикса; в новых проектах — не использовать inline-стили вообще.

5. **`buildMenu()` оставлен мёртвым после переключения на custom menu в renderer** — захламляет main.js.
   Решение: удалять сразу.

6. **Присваивать `canvas.width` где попало.** У `<canvas>` буферный размер (`width`/`height`) и экранный (`style.width`) независимы: присвоение `width` сбрасывает буфер, а CSS-размер остаётся от прошлого состояния, и картинка растягивается в старую рамку. Я сначала залатал это в одном `restoreHistory`, а потом нашёл ещё пять мест с тем же багом (обрезка, поворот, смена размера, два открытия файла). Правильно — одна функция `setCanvasSize(w, h)`, которая меняет буфер и сама зовёт `applyZoom()`, и ни одного прямого присваивания в коде. Общее правило для калькулятора: если у состояния есть «внутреннее» и «показанное» представление, синхронизировать их должна одна функция, а не дисциплина на каждом вызывающем месте.

7. **`eval()` для парсинга выражения** — НИКОГДА. Свой парсер.

8. **`text-size` input без clamp** — `min`/`max` HTML работают только для стрелок; вводимое руками значение не зажимается. Делать `Math.min(max, Math.max(min, parseInt(v)))` в input listener.

9. **Flood fill без множества посещённых пикселей** — ранний выход `if (target === fill) return` спасает только при непрозрачности 100%. При alpha < 1 и цвете заливки, близком к исходному, смешанный результат остаётся в пределах допуска `colorsMatch`, пиксель снова совпадает с target, соседи кладут его в стек заново — стек растёт бесконечно и приложение зависает. Всегда держать `Uint8Array(w*h)` посещённых.

10. **`restoreHistory` через `img.onload` без счётчика поколений** — несколько быстрых Ctrl+Z завершаются не в том порядке, на холсте оседает не тот кадр. Инкрементировать `restoreGeneration` на входе и проверять его в `onload`.

11. **`canvas.toDataURL('image/png')` до того, как известно расширение** — «Сохранить как JPEG» пишет PNG-байты под именем `.jpg`. Сначала путь, потом кодирование под его расширение.

12. **Две операции с противоположным смыслом под одним именем.** «Отменить подъём выделения» (вернуть пиксели на место) и «удалить выделение» (оставить дыру) — разные вещи, но обе выглядят как «убрать плавающий объект». Ctrl+X и Delete звали `cancelFloating`, и вырезание возвращало объект обратно вместо удаления. Заводить отдельные функции с говорящими именами (`cancelFloating` / `deleteFloating`), даже если тела почти совпадают.

13. **Копипаста ветвления по состоянию объекта.** Блок «нарисуй с поворотом, или с клипом, или просто» был скопирован в трёх местах. Стоило добавить поворот — и про него забыли в пипетке и в копировании: они рисовали неповёрнутый прямоугольник. Один рендер (`drawFloatingOnto`) на все места, где объект попадает на пиксели. То же для «выделить всё», скопированного в пункт меню, хоткей и обработчик IPC.

14. **Жёсткая подпись по индексу в списке с вытеснением.** Нулевая строка истории подписывалась «Исходное состояние» по `i === 0`, но при переполнении лимита нулевую запись вытесняет обычная правка, и подпись начинает врать. Подпись хранить в самой записи.

15. **Стек обхода из массивов пар (`[[x, y]]`)** в заливке и подобных обходах — на заливке во весь экран это миллионы короткоживущих объектов для сборщика мусора. Плоский массив `[x, y, x, y, ...]` с двумя `pop()` подряд.

16. **Оставлять настройку в разделяемом объекте состояния.** `ctx.globalAlpha` у
    `<canvas>` — состояние контекста, а не операции: инструмент присвоил
    `state.opacity` и ушёл, а значение осталось висеть. В него потом попадали все
    прямые записи на холст — отмена, очистка, поворот, отражение, открытие файла.
    Один Ctrl+Z после мазка с прозрачностью 40% делал весь лист прозрачным на 60%,
    а «Очистить холст» давал серый вместо белого. Это тот же урок, что и пункт 6
    про `canvas.width`, только злее: там разъезжались два представления одного
    состояния, здесь настройка одной операции протекает во все следующие.
    Лечится не сбросом на каждом вызывающем месте, а тем, чтобы настройки в
    разделяемом объекте не было вовсе: рисовать непрозрачно в отдельный буфер и
    накладывать альфу один раз при переносе. Тогда есть один проверяемый инвариант
    («`globalAlpha` всегда 1») вместо дисциплины в семи местах.

17. **Сравнивать `e.key` с буквой в нижнем регистре.** С зажатым Shift `e.key`
    приходит заглавной: `e.key === 's'` ложно для Ctrl+Shift+S. «Сохранить как»
    стояло в меню с подписью Ctrl ⇧ S и не работало вообще. Всегда
    `e.key.toLowerCase()`. Проверять при этом стоит не код, а меню: если
    сочетание где-то обещано пользователю, оно должно быть заведено.

18. **Экранный редактор поверх масштабируемого холста.** Позиция рамки умножалась
    на зум, а размер шрифта — нет, и на 200% буквы в рамке были вдвое мельче тех,
    что лягут на холст. Масштабировать элемент целиком одним
    `transform: scale(zoom)` с `transform-origin: 0 0` — тогда и `padding`
    остаётся в координатах холста, и смещение при печати не надо пересчитывать.

19. **Дробные границы выделения.** Подъём округлял размер снимка, а укладка
    обратно рисовала его в дробный прямоугольник: картинка пересэмплировалась
    дважды за одно перетаскивание и заметно мылилась. Приводить рамку к целым
    пикселям и зажимать границами холста сразу на отпускании мыши, а не при каждой
    последующей операции.

20. **Считать позицию экранного оверлея один раз, при показе.** Кнопки обрезки
    ставились под рамкой выделения в момент появления и больше не пересчитывались:
    первый же зум или панорама отрывали их от рамки. Всё, что привязано к
    координатам документа, но живёт в экранных пикселях, пересчитывается по одному
    и тому же событию — сводить в одну функцию (`updateOverlays`) и звать её
    целиком, а не перечислять оверлеи в каждом обработчике. Список, скопированный
    в четыре места, отставал в двух.

21. **Считать «пользователь что-то сделал» по факту вызова, а не по результату.**
    Подъём выделения стирает область сразу, поэтому «кликнул внутрь рамки, кликнул
    мимо» проходил полный цикл стирания и обратной отрисовки и писал в историю
    запись о перемещении, не изменив ни пикселя. Само по себе это лишь мусор в
    списке, но как только появился вопрос «сохранить перед выходом?», приложение
    начало спрашивать на ровном месте. Сравнивать состояние до и после, а не
    считать, что раз обработчик вызвался — что-то поменялось. Флаг «пользователь
    потащил», расставленный по обработчикам drag'а, решает ту же задачу хуже:
    забыть его в одном месте — значит молча потерять запись в истории.

22. **Числовое поле без верхней границы.** У поля размера холста стояла только
    нижняя проверка (`w < 10`). Лишний ноль при вводе — и `canvas.width = 99999`:
    буфер такого размера браузер не выделяет, но и не сообщает об этом, свойство
    честно возвращает записанное, а пиксели читаются как прозрачные. Рисунок
    потерян молча и безвозвратно. Проверять обе границы, и не только на сторону, но
    и на произведение: 20000×20000 проходит любую проверку «сторона не больше
    20000» и всё равно не выделяется. Для жестов (перетаскивание ручки) не
    отказывать, а зажимать: диалог посреди drag'а показывать некуда.

23. **Версия приложения, вписанная в интерфейс строкой.** «Paint Pro v1.0» стояло в
    статусбаре и в «О программе» и доехало неизменным до релиза 1.6.0 — при том, что
    в `package.json` версия обновлялась каждый раз. Тянуть из единственного
    источника: в Electron `app.getVersion()` через IPC, в .NET атрибуты сборки.
    Правило шире номера версии: если значение уже где-то есть, второй его копии в
    интерфейсе быть не должно.

24. **Убирать «весь белый» вместо «белого фона».** Кеинг прозрачности проходом по всем пикселям пробивает дыры внутри картинки — блик на фото, белую заливку фигуры, — и сквозь них видно нижний слой. Заливка от краёв области внутрь снимает фон вокруг рисунка и не трогает белое внутри.

25. **`dragleave` как сигнал «курсор покинул окно».** Событие стреляет и при переходе между дочерними элементами, поэтому подсветка зоны drop гаснет и залипает как попало. Считать глубину: `dragenter` увеличивает счётчик, `dragleave` уменьшает, гасить на нуле.

---

## 6. Чеклист «оно ощущается как Apple liquid glass»

- [ ] Цветной радиальный фон body (не однотонный).
- [ ] 8–12 фоновых пузырей с `bubbleRise` анимацией.
- [ ] Все основные панели: `backdrop-filter: blur(28px) saturate(190%)`.
- [ ] Панели имеют `::before` блик сверху и `box-shadow` outer + inner.
- [ ] `border-radius` 14–22px на всём — никаких острых углов.
- [ ] `.btn:hover` приподнимает на 1px.
- [ ] Активные/primary кнопки — accent-grad + accent-glow.
- [ ] Ripple на клике.
- [ ] Тултипы — стеклянные тёмные с blur(20px).
- [ ] Стеклянный scrollbar.
- [ ] Шрифт SF Pro / -apple-system, цифры с tabular-nums.
- [ ] z-index гигиена: пузыри 0, контент 2, dropdowns/tooltips 100.

---

## 7. Быстрый старт калькулятора (примерный package.json)

```json
{
  "name": "calc-pro",
  "version": "1.0.0",
  "main": "main.js",
  "scripts": {
    "start": "electron .",
    "build": "electron-builder --win portable --x64",
    "build-installer": "electron-builder --win nsis --x64"
  },
  "devDependencies": {
    "electron": "^33.4.11",
    "electron-builder": "^25.1.8"
  },
  "build": {
    "appId": "com.alexey.calcpro",
    "productName": "Calc Pro",
    "win": { "icon": "build/icon.ico", "target": ["portable", "nsis"] },
    "nsis": {
      "oneClick": false,
      "perMachine": false,
      "allowToChangeInstallationDirectory": true,
      "createDesktopShortcut": true,
      "createStartMenuShortcut": true
    }
  }
}
```

`main.js` — копия из Paint Pro с заменой имени файла на `calc-pro.html` и упрощением IPC (только save-file для экспорта истории).

---

## 8. Ссылки на источники

- Apple WWDC 2025 — Liquid Glass design language (iOS 26 / macOS Tahoe 26).
- CSS-tricks: «Getting Clarity on Apple's Liquid Glass» — https://css-tricks.com/getting-clarity-on-apples-liquid-glass/
- DEV.to: «Recreating Apple's Liquid Glass Effect with Pure CSS» — https://dev.to/kevinbism/recreating-apples-liquid-glass-effect-with-pure-css-3gpl
- Browser support: `backdrop-filter` отлично работает в Electron 33 (Chromium 130+). На Windows hardware acceleration включён по умолчанию — производительность нормальная даже с 12 анимированными пузырями + 4 стеклянными панелями.
