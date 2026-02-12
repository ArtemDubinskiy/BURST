/*
 * Проект:   BURST(Brutal Utilization & Resilience Stress Testing)
 * Автор:    Дубинский Артем {dub}
 */

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using BURST.Types.Base;

namespace BURST
{
    /// <summary>
    /// Хранилище агрегированной статистики ошибок по связке «ядро:тест».
    /// <para>Используется для:
    /// <list type="bullet">
    ///   <item><description>фиксации первой/последней ошибки, серий подряд и общего числа;</description></item>
    ///   <item><description>быстрого вывода «топа» проблемных связок в мониторе;</description></item>
    ///   <item><description>сигнализации факта появления ошибок (<see cref="Started"/>) для дополнительной маркировки UI.</description></item>
    /// </list>
    /// </para>
    /// <remarks>
    /// Все операции потокобезопасны: используется <see cref="ConcurrentDictionary{TKey, TValue}"/>.
    /// Счётчики модифицируются на месте; для общей суммы и флага - атомарные операции <see cref="System.Threading.Interlocked"/>.
    /// </remarks>
    /// </summary>
    internal static class ErrorState
    {
        /// <summary>
        /// Флаг «ошибки хотя бы раз фиксировались».
        /// 0 - не было ошибок, 1 - хотя бы одна ошибка зарегистрирована.
        /// </summary>
        public static volatile int Started = 0;

        /// <summary>
        /// Глобальный счётчик всех ошибок (для сводки).
        /// Инкрементируется атомарно.
        /// </summary>
        public static long Total;

        /// <summary>
        /// Карта счётчиков по ключу «ядро:имя_типа_теста».
        /// Пример: "3:Avx2CacheStressTest".
        /// </summary>
        public static readonly ConcurrentDictionary<string, Counters> Map = new();

        /// <summary>
        /// Сериализуемый набор счётчиков/метаданных по ошибкам конкретной связки.
        /// </summary>
        internal sealed class Counters
        {
            public int Consecutive;                    // длина текущей серии подряд идущих ошибок без успешной валидации
            public int Total;                          // общий счётчик ошибок для связки
            public DateTime FirstAt = DateTime.MinValue; // время первой ошибки в серии (или вообще первой)
            public DateTime LastAt = DateTime.MinValue;  // время последней ошибки
            public string LastMessage = "";            // сообщение последнего исключения
        }

        /// <summary>
        /// Регистрирует новую ошибку по связке «ядро+тест».
        /// Увеличивает серию и общий счётчик, сохраняет метки времени и текст последнего исключения.
        /// Также увеличивает глобальный <see cref="Total"/> и поднимает флаг <see cref="Started"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Report(int coreIndex, IStressTest test, Exception ex)
        {
            string key = $"{coreIndex}:{test.GetType().Name}";
            var c = Map.AddOrUpdate(key,
                _ => new Counters { Consecutive = 1, Total = 1, FirstAt = DateTime.Now, LastAt = DateTime.Now, LastMessage = ex.Message },
                (_, old) =>
                {
                    old.Consecutive++;
                    old.Total++;
                    old.LastAt = DateTime.Now;
                    old.LastMessage = ex.Message;
                    return old;
                });

            System.Threading.Interlocked.Increment(ref Total);
            System.Threading.Interlocked.Exchange(ref Started, 1);
        }

        /// <summary>
        /// Сбрасывает счётчик <see cref="Counters.Consecutive"/> для связки «ядро+тест»,
        /// что сигнализирует о том, что после ошибок был успешный цикл RunTest/Validate.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ResetOk(int coreIndex, IStressTest test)
        {
            string key = $"{coreIndex}:{test.GetType().Name}";
            if (Map.TryGetValue(key, out var c))
            {
                c.Consecutive = 0;
                c.FirstAt = DateTime.MinValue; // обнуляем «начало серии»
            }
        }
    }
}
