/*
 * Проект:   BURST(Brutal Utilization & Resilience Stress Testing)
 * Автор:    Дубинский Артем {dub}
 */

using System.Collections.Concurrent;
using BURST.Utils.Log.Types;

namespace BURST
{
    /// <summary>
    /// Централизованное глобальное состояние приложения (вынесено из Program).
    /// <para>Содержит:
    /// <list type="bullet">
    ///   <item><description><see cref="CancelRequested"/> - флаг мягкой остановки всех рабочих циклов (устанавливается Ctrl+C/Enter).</description></item>
    ///   <item><description><see cref="ErrorQueue"/> - потокобезопасная очередь ошибок, которые монитор выводит пользователю.</description></item>
    ///   <item><description><see cref="MonitorReady"/> - барьер готовности, который сообщает, что монитор инициализирован и можно запускать нагрузку.</description></item>
    ///   <item><description><see cref="UiCore"/> - опциональный логический индекс ядра, зарезервированный для UI/монитора (в текущей ревизии <c>null</c>, резерва нет).</description></item>
    /// </list>
    /// </para>
    /// <remarks>
    /// Такой подход минимизирует связность между подсистемами и упрощает управление жизненным циклом потоков.
    /// </remarks>
    /// </summary>
    internal static class AppState
    {
        /// <summary>
        /// Признак «просим остановить все рабочие циклы».
        /// <br/>volatile - чтобы изменения были видны всем потокам без дополнительных барьеров.
        /// </summary>
        public static volatile bool CancelRequested = false;

        /// <summary>
        /// Очередь для межпоточной публикации ошибок (монитор читает и выводит).
        /// </summary>
        public static readonly ConcurrentQueue<Exception> ErrorQueue = new();

        /// <summary>
        /// Событие-флаг: монитор полностью инициализировался (счётчики созданы и прогреты).
        /// Потоки нагрузки ожидают этот сигнал для корректного старта.
        /// </summary>
        public static readonly ManualResetEventSlim MonitorReady = new(false);

        /// <summary>
        /// Логический индекс ядра, предназначенный для (возможного) пиннинга монитора.
        /// <br/>В текущей реализации резервирование отключено: остаётся <c>null</c>.
        /// </summary>
        public static int? UiCore = null;

        /// <summary>
        /// Карта прогресса тестов по ядрам:
        /// ключ - логический индекс ядра, значение - список записей прогресса по каждому тесту.
        /// </summary>
        public static readonly ConcurrentDictionary<int, List<TestProgress>> TestProgressMap = new();
    }
}
