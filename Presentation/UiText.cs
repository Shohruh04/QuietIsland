using System;
using System.Collections.Generic;

namespace QuietIsland;

internal static class UiText
{
    public static string Language { get; set; } = "en";
    private static readonly Dictionary<string, string> English = new(StringComparer.Ordinal)
    {
        ["Показать виджет"] = "Show widget", ["Скрыть"] = "Hide", ["Расположение"] = "Placement",
        ["Слева"] = "Left", ["Справа"] = "Right", ["Автоматически: больше свободного места"] = "Automatic: largest free space",
        ["Монитор"] = "Monitor", ["Основной монитор"] = "Primary monitor", ["Язык"] = "Language",
        ["Размер"] = "Size", ["Своя ширина…"] = "Custom width…", ["Ширина виджета"] = "Widget width",
        ["Ширина (280–1200 DIP)"] = "Width (280–1200 DIP)", ["Применить"] = "Apply", ["Отмена"] = "Cancel",
        ["О программе"] = "About", ["Релизы и обновления"] = "Releases and updates", ["Исходный код · MIT"] = "Source code · MIT",
        ["Проверить обновления…"] = "Check for updates…", ["Автоматически проверять обновления"] = "Automatically check for updates",
        ["Проверка обновлений…"] = "Checking for updates…", ["Проверка / загрузка обновления…"] = "Checking / downloading update…",
        ["Доступна версия {0}"] = "Version {0} available", ["Загрузка обновления…"] = "Downloading update…", ["Проверка файла…"] = "Verifying file…",
        ["Установлена последняя версия QuietIsland."] = "The latest version of QuietIsland is installed.",
        ["Доступна QuietIsland {0}. Выберите «Проверить обновления» в меню значка."] = "QuietIsland {0} is available. Choose Check for updates in the tray menu.",
        ["Доступна QuietIsland {0}. Открыть страницу загрузки? Portable-версию нужно распаковать вручную."] = "QuietIsland {0} is available. Open the download page? Portable updates must be extracted manually.",
        ["Доступна QuietIsland {0}. Скачать обновление и открыть установщик? Настройки сохранятся."] = "QuietIsland {0} is available. Download the update and open the installer? Your settings will be preserved.",
        ["Не удалось проверить или установить обновление. Проверьте подключение и повторите позже."] = "Could not check for or install the update. Check your connection and try again later.",
        ["По центру"] = "Center", ["Своя позиция…"] = "Custom position…", ["Положение виджета"] = "Widget position",
        ["Положение (0% слева — 100% справа)"] = "Position (0% left — 100% right)",
        ["Цветовая тема"] = "Color theme", ["Обсидиан"] = "Obsidian", ["Графит"] = "Graphite", ["Сланец"] = "Slate",
        ["Хвойный"] = "Evergreen", ["Эспрессо"] = "Espresso", ["Слива"] = "Plum", ["Фарфор · светлая"] = "Porcelain · light",
        ["Прозрачность фона"] = "Background transparency", ["Непрозрачный фон"] = "Opaque background",
        ["Источник музыки"] = "Music source", ["Автоматически"] = "Automatic", ["Яндекс Музыка"] = "Yandex Music",
        ["Показывать строку песни"] = "Show current lyric", ["Онлайн-текст · LRCLIB"] = "Online lyrics · LRCLIB",
        ["Для поиска в LRCLIB передаются название, исполнитель, альбом и длительность песни."] = "LRCLIB receives the song title, artist, album and duration to find lyrics.",
        ["Обновить текст песни"] = "Refresh lyrics", ["Выбрать версию текста"] = "Choose lyrics version",
        ["Нет вариантов: обновите текст песни"] = "No versions: refresh lyrics", ["Синхронизация текста"] = "Lyrics synchronization",
        ["Показать строку раньше на 0,5 с"] = "Show lyrics 0.5 seconds earlier", ["Показать строку позже на 0,5 с"] = "Show lyrics 0.5 seconds later",
        ["Сбросить сдвиг"] = "Reset offset", ["Загрузить локальный файл .lrc…"] = "Import local .lrc file…",
        ["Показывать изменение громкости"] = "Show volume changes", ["Скрывать в полноэкранном режиме"] = "Hide in fullscreen",
        ["Таймер фокусировки"] = "Focus timer", ["Пауза / продолжить"] = "Pause / resume", ["Остановить"] = "Stop",
        ["Запускать вместе с Windows"] = "Start with Windows", ["Открыть папку программы"] = "Open application folder", ["Выход"] = "Exit",
        ["Таймер завершён. Пора отдохнуть."] = "Timer finished. Time for a break.", ["Звук выключен"] = "Muted",
        ["Включите музыку в любимом плеере"] = "Play music in your favorite player", ["Включите музыку"] = "Play some music",
        ["Плеер не разрешил перемотку"] = "The player rejected seeking", ["Предыдущий трек"] = "Previous track",
        ["Следующий трек"] = "Next track", ["Воспроизвести / пауза"] = "Play / pause", ["Позиция воспроизведения"] = "Playback position",
        ["Текст песни / исполнитель"] = "Lyrics / artist", ["Показать текст песни"] = "Show lyrics",
        ["Текст для текущей песни"] = "Lyrics for the current song", ["Синхронный текст (*.lrc)|*.lrc"] = "Synchronized lyrics (*.lrc)|*.lrc",
        ["В файле нет временных меток"] = "The file has no timestamps", ["Сначала выберите песню"] = "Select a song first",
        ["Локальный синхронный текст"] = "Local synchronized lyrics", ["Выбранная версия · LRCLIB"] = "Selected version · LRCLIB",
        ["Поиск текста песни…"] = "Finding lyrics…", ["Проверка локального текста…"] = "Checking local lyrics…",
        ["Онлайн-текст отключён"] = "Online lyrics disabled", ["Сервис текста не ответил"] = "The lyrics service did not respond",
        ["Не удалось загрузить текст песни"] = "Could not load lyrics", ["Сервис текста занят. Повторите позже."] = "Lyrics service busy. Try again later.",
        ["Текст этой песни не найден"] = "No lyrics found for this song", ["Выберите версию текста в меню"] = "Choose a lyrics version in the menu",
        ["Инструментальная композиция"] = "Instrumental", ["Синхронный текст · LRCLIB"] = "Synchronized lyrics · LRCLIB",
        ["Текст без временных меток"] = "Lyrics without timestamps", ["Медиа"] = "Media",
        ["Настройки"] = "Settings", ["Настройки…"] = "Settings…", ["Закрыть"] = "Close",
        ["Закрепить положение"] = "Lock position", ["Цвет обложки"] = "Album color",
        ["Использовать цвет обложки"] = "Use album color",
        ["Ваш маленький остров спокойствия."] = "Your little island of calm.",
        ["Внешний вид"] = "Appearance", ["Поведение"] = "Behavior", ["ПРЕДПРОСМОТР"] = "LIVE PREVIEW",
        ["Изменения сразу видны в виджете. Предпросмотр масштабируется по ширине."] = "Changes appear on your island instantly. Preview scales to fit.",
        ["Цвет текущей обложки в рамке и полосе прогресса."] = "Use the artwork color for the border and progress bar.",
        ["Непрозрачный"] = "Opaque", ["Прозрачный"] = "Transparent",
        ["Положение на панели задач"] = "Taskbar position", ["Своя позиция"] = "Custom position",
        ["Защитить виджет от случайного перемещения мышью."] = "Keep your island in place while using its controls.",
        ["Отключённый монитор"] = "Disconnected monitor", ["Текст песни"] = "Lyrics", ["Общие"] = "General",
        ["Положительный сдвиг показывает строки раньше."] = "Positive offsets show lyrics earlier.", ["с"] = "s",
        ["Версия"] = "Version", ["Обновления"] = "Updates", ["Проект"] = "Project",
        ["Музыка, текст песни и немного спокойствия — прямо на панели задач."] = "Music, lyrics, and a little calm — right on your taskbar.",
        ["Нативное приложение для Windows. Без лишних окон, постоянных анимаций и отвлекающих элементов."] = "A native Windows app that keeps things light, quiet, and close at hand.",
        ["Тихая проверка раз в день. Установка — только с вашего согласия."] = "A quiet check once a day. Installation always asks you first.",
        ["Проверяйте новые версии здесь или включите автоматическую проверку."] = "Check for a new version here, or enable automatic checks.",
        ["Показать виджет: Ctrl+Alt+I · Скрыть: Ctrl+Alt+D"] = "Show island: Ctrl+Alt+I · Toggle visibility: Ctrl+Alt+D",
        ["Изменения сохраняются автоматически"] = "Changes are saved automatically"
    };
    public static string T(string text) => Translate(text, Language);
    internal static string Translate(string text, string language)
    {
        if (language != "en") return text;
        if (English.TryGetValue(text, out var result)) return result;
        // Numeric menu labels and status values retain their numbers/shortcuts.
        foreach (var (ru, en) in new[] { ("% прозрачности", "% transparent"), (" минут", " minutes"), ("Громкость: ", "Volume: "), ("Перемотка · ", "Seeking · "), ("Фокус · ", "Focus · ") })
            if (text.Contains(ru, StringComparison.Ordinal)) return text.Replace(ru, en, StringComparison.Ordinal);
        var shortcut = text.IndexOf("  ", StringComparison.Ordinal);
        return shortcut > 0 && English.TryGetValue(text[..shortcut], out result) ? result + text[shortcut..] : text;
    }
}
