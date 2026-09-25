<div align="center">

<img src="paint-pro-electron/build/icon.png" width="96" alt="">

# Paint Pro

**Растровый графический редактор для Windows, написанный дважды: один раз как единственный HTML-файл внутри Electron, второй — нативно на C# 12 с WPF и SkiaSharp. Обе версии развиваются рядом, и правило, найденное на одной стороне, переносится на другую.**

[**Попробовать в браузере →**](https://alexalesha.github.io/PaintPro/) &nbsp;·&nbsp; [Скачать для Windows](https://github.com/ALEXalesha/PaintPro/releases/latest) &nbsp;·&nbsp; [English version of this file](README.md)

[![CI](https://github.com/ALEXalesha/PaintPro/actions/workflows/ci.yml/badge.svg)](https://github.com/ALEXalesha/PaintPro/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/ALEXalesha/PaintPro?color=6c5ce7)](https://github.com/ALEXalesha/PaintPro/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/ALEXalesha/PaintPro/total?color=22a7e0)](https://github.com/ALEXalesha/PaintPro/releases)
[![License](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

<img src="paint-pro-electron/docs/screenshots/hero.png" width="900" alt="Paint Pro, Electron-версия">

</div>

## Две версии одного редактора

| | Electron | C# |
| --- | --- | --- |
| Папка | [`paint-pro-electron/`](paint-pro-electron/README.ru.md) | [`src/`](docs/CSHARP.ru.md), [`tests/`](tests/PaintPro.Tests) |
| Стек | один HTML-файл, canvas, чистый JS; Electron даёт окно, меню и диалоги файлов | C# 12, WPF, .NET 8, SkiaSharp, MVVM, Command Pattern |
| Сборки | установщик, portable и один `.html`, который работает в любом браузере без сети | установщик и self-contained single `.exe` |
| Версия | 1.20.0 | 1.35.0 |
| Проверки | 469 проверок Playwright в Chromium + 17 по настоящему приложению | 1141 тест xUnit, среди них два фаззера |
| Документация | [README](paint-pro-electron/README.ru.md), [устройство](paint-pro-electron/docs/ARCHITECTURE.md) | [README](docs/CSHARP.ru.md) ([English](docs/CSHARP.md)) |

У обеих девятнадцать инструментов (от руки, заливка и пипетка, текст, восемь фигур,
прямоугольное и четырёхточечное выделение, кадрирование, рука), слои с прозрачностью, пять
тем, плавающий объект, который тянется, растягивается и вращается, и лента истории, в
которой можно выключить любую правку из середины — документ пересобирается без неё.

<img src="docs/screenshots/hero.png" width="900" alt="Paint Pro, C#-версия">

## Зачем две

Первой была Electron-версия. C#-версия начиналась как перенос с более чистой архитектурой:
слои, MVVM, команды с diff-битмапами для отмены, один enum режима вместо кучи флагов. С тех
пор версии держатся равными по возможностям, и большая часть правил в каждой найдена на
соседней стороне. `paint-pro-electron/tests/parity.spec.js` назван ровно про это: каждая
проверка подписана выпуском, в котором правило нашлось в C#.

## Баги находят проверки

Ни один набор — не юнит-тесты вокруг функций. Проверки Electron открывают страницу в
Chromium, водят настоящей мышью по холсту и читают пиксели. Тесты C# водят настоящие
элементы WPF в STA-потоке, читают `MainWindow.xaml` и проверяют каждую привязку и хоткей,
а ленту истории гоняют фаззером на случайных последовательностях правок. Самый сильный
инвариант в обеих: *позиция курсора в истории однозначно задаёт документ, каким бы путём до
неё ни дошли*.

[`CHANGELOG.md`](CHANGELOG.md) рассказывает каждый выпуск с объяснениями. Большая часть
записей начинается с того, что нашла проверка или сплошной проход, а не с того, что
планировалось.

## Кадры собирает программа

Ни одна картинка здесь не снята с экрана. `paint-pro-electron/tools/make-screenshots.js`
запускает настоящее приложение через Playwright и рисует настоящими движениями мыши.
`tools/PaintPro.Screenshots` открывает настоящее окно WPF за краем экрана, рисует
настоящими инструментами тем же путём, что и мышь, и снимает окно само. Первые же кадры
C#-версии нашли две ошибки, исправленные в 1.27.0: половина иконок инструментов
оставалась белой в светлой теме, а стандартные светло-серые полосы прокрутки резали тёмное
стекло.

## Выпуски

В каждом выпуске сборки обеих версий. До 1.15.0 здесь публиковалась только
Electron-версия и теги шли по её номерам; с 1.27.0 тег идёт по номеру C#-версии, а номер
Electron-версии стоит в названии выпуска и в именах файлов.

Windows 10 или 11, x64. Сборки не подписаны, поэтому SmartScreen предупреждает при первом
запуске.

## Сборка

```powershell
# Electron
cd paint-pro-electron; npm install; npm start

# C#
dotnet run --project src/PaintPro.Wpf -c Release
dotnet test tests/PaintPro.Tests
```

Упаковка обеих описана в их README.

## Лицензия

MIT, см. [LICENSE](LICENSE).
