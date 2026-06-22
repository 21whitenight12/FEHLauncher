[README.md](https://github.com/user-attachments/files/29214512/README.md)
<div align="center">
  <img src="ico.ico" alt="FEH Launcher" width="80" height="80">

  # FEH Launcher 🚀

  **Fallout 4 Launcher** — современный лаунчер для сборки Fallout Event Horizon с поддержкой Mod Organizer 2 и F4SE.

  <p>
    <a href="https://github.com/Wh1teNight30/FEHLauncher/releases">
      <img src="https://img.shields.io/badge/версия-1.2.4-blue?style=flat-square" alt="Version">
    </a>
    <a href="https://dotnet.microsoft.com/download/dotnet/8.0">
      <img src="https://img.shields.io/badge/.NET-8.0-purple?style=flat-square" alt=".NET">
    </a>
    <a href="https://github.com/Wh1teNight30/FEHLauncher/blob/master/LICENSE">
      <img src="https://img.shields.io/badge/лицензия-MIT-green?style=flat-square" alt="License">
    </a>
    <a href="https://discord.gg/UsCu5gXCJS">
      <img src="https://img.shields.io/badge/Discord-присоединиться-5865F2?style=flat-square&logo=discord&logoColor=white" alt="Discord">
    </a>
  </p>
</div>

---

## ✨ Возможности

- **🎮 Автопоиск Fallout 4** — сканирование реестра и Steam
- **⚡ Запуск через F4SE** — с полной настройкой MO2-профиля
- **📦 Дельта-обновление сборки** — SHA256-верификация, параллельная закачка (3 потока)
- **📋 Панель опциональных модов** — управление модами через MO2 API Bridge
- **🔄 Автообновление лаунчера** — через GitHub Releases
- **📚 Автопроверка библиотек** — DirectX 12, VC++ Redist, XNA 4.0, .NET 4.8.1
- **🌗 Тёмная и светлая темы** — тёплая бумажная палитра для светлой
- **🎨 Акцентный цвет Windows** — динамическая интеграция
- **📊 Подсчёт размера сохранений** — предупреждение при превышении 10 ГБ
- **📝 Коллапсируемый лог** — с автоскроллом

## 🧰 Использование

### Запуск игры

Лаунчер автоматически:
1. Находит установленную Fallout 4 (реестр / Steam)
2. Использует нужный профиль в MO2
3. Находит `f4se_loader.exe` в Root Builder
4. Запускает MO2 с F4SE

**Флаги запуска:**
| Флаг | Описание |
|------|---------|
| `--skip-f4se` | Запуск без F4SE |

### Управление модами

Моды с пометкой `[O]` в названии отображаются в боковой панели «Mods».
Подключение к MO2 через TCP-мост (порт 52525) — без экспорта списков вручную.

### Обновление сборки

Кнопка Build в статус-баре показывает доступные обновления сборки.
Дельта-механизм скачивает только изменившиеся файлы, проверяя SHA256.

### Проверка библиотек

При первом запуске лаунчер проверяет наличие системных библиотек:

| Библиотека | Источник |
|-----------|---------|
| DirectX 12 | `distrib.whitenight-server.com/DX12/` |
| Visual C++ Redist (2005-2026) | `distrib.whitenight-server.com/VisualCppRedist_AIO_x86_x64.exe` |
| XNA Framework 4.0 | `distrib.whitenight-server.com/xnafx40_redist.msi` |
| .NET Framework 4.8.1 | `distrib.whitenight-server.com/NDP481-Web.exe` |

---

## 🛠️ Технологии

| Технология | Назначение |
|-----------|-----------|
| **.NET 8.0** | Среда выполнения (Self-Contained Single File) |
| **WPF** | Графический интерфейс |
| **C#** | Язык программирования |
| **MO2 API Bridge** | TCP-управление модами (порт 52525) |
| **GitHub API** | Проверка и загрузка обновлений |

---

## 📁 Структура проекта

```
FEHLauncher/
├── App.xaml(.cs)          # Точка входа
├── MainWindow.xaml(.cs)   # Главное окно (~1700 строк)
├── Mo2BridgeClient.cs     # TCP-клиент MO2 API Bridge
├── ModsPanel.xaml(.cs)    # Панель опциональных модов
├── UpdateChecker.cs       # GitHub-обновления
├── BuildManifest.cs       # Модель манифеста сборки
├── BuildUpdater.cs        # Дельта-обновление сборки
├── PrerequisiteChecker.cs # Проверка системных библиотек
├── generate-manifest.py   # Генератор manifest.json (сервер)
└── CHANGELOG.md           # История изменений
```

---

## 📜 История версий

| Версия | Дата | Описание |
|--------|------|----------|
| v1.2.4 | 22.06.2026 | Prerequisite Checker + UI fixes |
| v1.2.3 | 21.06.2026 | Build Updater v2 + UX overhaul |
| v1.2.2 | 19.06.2026 | Патчноут, автообновление, Expert Mode |
| v1.2.0 | 14.06.2026 | MO2 API Bridge + UX improvements |
| v1.0.0 | 10.06.2026 | Первый стабильный релиз |

> Полный список изменений: [CHANGELOG.md](CHANGELOG.md)

---

## 🤝 Вклад в проект

Предложения и pull request'ы приветствуются!  
По вопросам и багам — [Issues](https://github.com/Wh1teNight30/FEHLauncher/issues) или Discord.

---

## 📬 Контакты

- **Discord-сервер сборки:** [discord.gg/UsCu5gXCJS](https://discord.gg/UsCu5gXCJS)
- **Автор:** WhiteNight

---

<div align="center">
  <sub>Сделано с ❤️ для сообщества Fallout 4</sub>
</div>
