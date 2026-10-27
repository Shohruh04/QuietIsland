using System;
using System.Collections.Generic;

namespace QuietIsland;

internal static class UiText
{
    public static string Language { get; set; } = "ru";
    private static readonly Dictionary<string, string> English = new(StringComparer.Ordinal)
    {
        ["Показать виджет"] = "Show widget", ["Скрыть"] = "Hide", ["Расположение"] = "Placement",
        ["Слева"] = "Left", ["Справа"] = "Right", ["Автоматически: больше свободного места"] = "Automatic: largest free space",
        ["Монитор"] = "Monitor", ["Основной монитор"] = "Primary monitor", ["Язык"] = "Language",
        ["Размер"] = "Size", ["Своя ширина…"] = "Custom width…", ["Ширина виджета"] = "Widget width",
        ["Ширина (280–1200 DIP)"] = "Width (280–1200 DIP)", ["Применить"] = "Apply", ["Отмена"] = "Cancel",
        ["О программе"] = "About", ["Релизы и обновления"] = "Releases and updates", ["Исходный код · MIT"] = "Source code · MIT",
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
        ["Текст без временных меток"] = "Lyrics without timestamps", ["Медиа"] = "Media"
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
