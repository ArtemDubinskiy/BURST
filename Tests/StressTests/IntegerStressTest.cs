/*
 * Проект:   BURST(Brutal Utilization & Resilience Stress Testing)
 * Автор:    Дубинский Артем {dub}
 */

using System.Runtime.CompilerServices;
using System.Numerics;           // BitOperations.RotateLeft
using BURST.Types.Base;

namespace BURST.Types.StressTests
{
    /// <summary>
    /// Тест на целочисленные вычисления (integer-only, без FPU/SIMD FP).
    /// <para>
    /// Цели:
    /// <list type="bullet">
    ///   <item><description>Максимально загрузить целочисленные ALU-пайплайны (add/xor/shift/rotate/mul) без влияния памяти и FPU.</description></item>
    ///   <item><description>Исключить ветвления в горячем цикле (branchless), держать данные в регистрах, минимизировать обращения к памяти.</description></item>
    ///   <item><description>Создать высокий ILP: несколько независимых «дорожек» вычислений (4 параллельные цепочки), частичная ручная развёртка.</description></item>
    /// </list>
    /// </para>
    /// <remarks>
    /// - Объём работы внутри <see cref="RunTest"/> фиксирован константой <see cref="InnerIterations"/> - внешний счётчик циклов задаётся инфраструктурой (см. параметр <c>--cycles</c>).
    /// - Для валидации используем независимую лёгкую проверку (гауссова сумма 1..1000), чтобы не повторять тяжёлую работу.
    /// - Все операции строго целочисленные: <c>ulong</c>/<c>long</c>/<c>int</c>, никаких float/double.
    /// </remarks>
    /// </summary>
    public sealed class IntegerStressTest : IStressTest
    {
        // === Конфигурация «горячей» части ===
        // Чем больше значение - тем сильнее нагрузка за один внешний цикл RunTest().
        // 2_000_000 даёт хорошую нагрузку, но при необходимости можно варьировать.
        private const int InnerIterations = 2_000_000;

        // 4 независимых «дорожки» для увеличения ILP (instruction-level parallelism).
        // Независимые аккумуляторы уменьшают зависимости по данным и улучшают загрузку ALU.
        private ulong _chk;      // сводный контрольный накопитель (не для Validate, но полезен как «разогрев» и детерминизм)

        // Поля для Validate - отделяем лёгкую детерминированную проверку.
        private long _sum1to1000;
        private bool _passed;

        /// <summary>
        /// Основная целочисленная нагрузка.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void RunTest()
        {
            // Инициализация независимых регистровых дорожек (семена подобраны как «хорошие» константы).
            // Все - чисто целочисленные константы; производим большое количество add/xor/shift/rotate/mul.
            ulong a = 0x9E3779B97F4A7C15ul; // φ*2^64 (golden ratio) - часто используется для перемешивания
            ulong b = 0xC2B2AE3D27D4EB4Ful; // mix-константы из xxHash
            ulong c = 0x165667B19E3779F9ul;
            ulong d = 0xD6E8FEB86659FD93ul;

            // Дополнительные «соли», чтобы включить разные типы целочисленных инструкций.
            const ulong m1 = 0xBF58476D1CE4E5B9ul;   // умножения (mul) - чисто целочисленные
            const ulong m2 = 0x94D049BB133111EBul;   // ещё одна хорошая мультипликативная константа
            const int R1 = 13, R2 = 17, R3 = 43;    // углы поворота (rotate) - чисто битовые операции

            // Сводный накопитель - чтобы результат «жили» и не был выкинут JIT'ом
            ulong acc = _chk;

            // Горячий цикл: без ветвлений, с ручной частичной развёрткой.
            // Все операции - integer: add, xor, shifts, rotates, mul. Никаких обращений к памяти / массивам.
            // unchecked - намеренно разрешаем переполнения без исключений (это часть нагрузки).
            unchecked
            {
                int i = 0;
                // Обработка по 8 «микрошагов» за итерацию - компромисс между читаемостью и IPC.
                // Каждый «микрошаг» трогает все 4 дорожки (a,b,c,d), что даёт N независимых зависимостей.
                for (; i <= InnerIterations - 8; i += 8)
                {
                    Step(ref a, ref b, ref c, ref d, m1, m2, R1, R2, R3, ref acc);
                    Step(ref a, ref b, ref c, ref d, m2, m1, R2, R3, R1, ref acc);
                    Step(ref a, ref b, ref c, ref d, m1, m2, R3, R1, R2, ref acc);
                    Step(ref a, ref b, ref c, ref d, m2, m1, R1, R3, R2, ref acc);

                    Step(ref a, ref b, ref c, ref d, m1, m2, R1, R2, R3, ref acc);
                    Step(ref a, ref b, ref c, ref d, m2, m1, R2, R3, R1, ref acc);
                    Step(ref a, ref b, ref c, ref d, m1, m2, R3, R1, R2, ref acc);
                    Step(ref a, ref b, ref c, ref d, m2, m1, R1, R3, R2, ref acc);
                }
                // Хвост (≤7 шагов)
                for (; i < InnerIterations; i++)
                {
                    Step(ref a, ref b, ref c, ref d, m1, m2, R1, R2, R3, ref acc);
                }
            }

            // Финальное свёртывание - чисто integer.
            _chk = acc ^ BitOperations.RotateLeft(a + b, 17) ^ BitOperations.RotateLeft(c + d, 29);

            // ЛЁГКАЯ детерминированная проверка: сумма 1..1000 (целочисленно, без ветвлений/памяти).
            // (Она независима от «горячего» цикла - дешёвая и надёжная.)
            long sum = 0;
            for (int k = 1; k <= 1000; k++)
                sum += k;
            _sum1to1000 = sum;

            _passed = true; // флаг ставим, пока Validate не опровергнет.
        }

        /// <summary>
        /// Одна «микрошаговая» трансформация на четырёх независимых дорожках.
        /// Выполняет только целочисленные операции (add/xor/shift/rotate/mul) без ветвлений.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Step(ref ulong a, ref ulong b, ref ulong c, ref ulong d,
                                 ulong mA, ulong mB, int r1, int r2, int r3, ref ulong acc)
        {
            // Немного «xorshift*-подобной» мешанины + RotateLeft (тоже integer).
            a ^= a >> 29; a *= mA; a = BitOperations.RotateLeft(a, r1);
            b ^= b >> 31; b *= mB; b = BitOperations.RotateLeft(b, r2);
            c ^= c >> 33; c *= mA; c = BitOperations.RotateLeft(c, r3);
            d ^= d >> 25; d *= mB; d = BitOperations.RotateLeft(d, r1);

            // Перекрёстное перемешивание через add/xor - даёт разные зависимости данных.
            a += b; c ^= d; b += c; d ^= a;

            // Усложняем зависимости и задействуем разные блоки целочисленных ALU.
            a *= 3; b *= 5; c *= 9; d *= 7;

            // Сводный аккумулятор, чтобы значения «жили» и не выкидывались оптимизатором.
            acc ^= a + (b << 1) ^ (c << 2) ^ (d << 3);
        }

        /// <summary>
        /// Лёгкая детерминированная проверка целостности.
        /// </summary>
        public void Validate()
        {
            // Проверяем «гауссову сумму» 1..1000: n*(n+1)/2
            const long n = 1000;
            long expected = (n * (n + 1)) / 2;
            if (_sum1to1000 != expected)
                _passed = false;

            if (!_passed)
                throw new Exception("Ошибка целочисленных вычислений: сумма от 1 до 1000 неверна.");
        }
    }
}
