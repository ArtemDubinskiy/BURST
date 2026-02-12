/*
 * Проект:   BURST(Brutal Utilization & Resilience Stress Testing)
 * Автор:    Дубинский Артем {dub}
 */
using System;
using System.Numerics;                 // Math.FusedMultiplyAdd, BitOperations.RotateLeft (для int, но не используем тут)
using System.Runtime.CompilerServices; // MethodImplOptions
using BURST.Types.Base;

namespace BURST.Types.StressTests
{
    /// <summary>
    /// Тест на вычисления с плавающей точкой (double), сфокусированный на загрузке FPU:
    /// <list type="bullet">
    ///   <item>горячий цикл без ветвлений и без памяти;</item>
    ///   <item>4 независимые регистральные цепочки (высокий ILP), частичная развёртка;</item>
    ///   <item>операции: add, mul, div, sqrt, FMA (если поддерживается JIT/CPU через Math.FusedMultiplyAdd);</item>
    ///   <item>контролируем диапазон значений, чтобы избежать NaN/Inf и денормалов;</item>
    ///   <item>валидация дёшевая и детерминированная: сумма 1..1000 в double даёт точно 500500 (в пределах точности IEEE754).</item>
    /// </list>
    /// </summary>
    public sealed class FloatingPointStressTest : IStressTest
    {
        // Чем больше - тем «тяжелее» один внешний прогон RunTest().
        // Общий метраж задаётся через --cycles на верхнем уровне.
        private const int InnerIterations = 2_000_000;

        // Аккумуляторы, чтобы значения «жили» между прогонами и не были выкинуты оптимизатором.
        private double _acc;

        // Для Validate:
        private double _finiteProbe; // «зонд» - итог горячего цикла; должен быть конечным числом
        private bool _passed;

        /// <summary>
        /// Основная FPU-нагрузка: чисто double-операции без ветвлений и без обращений к памяти.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void RunTest()
        {
            // 4 независимые дорожки - высокий ILP; начальные «семена» подобраны так, чтобы
            // не сваливаться в простые шаблоны и оставаться в хорошем численном диапазоне.
            double a = 1.6180339887498948482;  // φ
            double b = 2.7182818284590452354;  // e
            double c = 3.1415926535897932385;  // π
            double d = 0.5772156649015328606;  // γ (Эйлера–Маскерони)

            // Константы для смешивания и нормализации диапазона.
            const double k1 = 0.4142135623730950488; // √2 - 1
            const double k2 = 1.7320508075688772935; // √3
            const double eps = 1e-12;                // защита от деления на 0 и денормалов
            const double scale = 0.999999997;        // слабая «усадка», чтобы насыщать конвейер div/sqrt, не убегая в Inf

            double acc = _acc;

            unchecked // переполнения double не дают исключений, но оставляем семантику очевидной
            {
                int i = 0;
                // Частичная развёртка на 8 «микрошагов» за итерацию - лучшее IPC без потери читаемости.
                for (; i <= InnerIterations - 8; i += 8)
                {
                    Step(ref a, ref b, ref c, ref d, k1, k2, eps, scale, ref acc);
                    Step(ref a, ref b, ref c, ref d, k2, k1, eps, scale, ref acc);
                    Step(ref a, ref b, ref c, ref d, k1, k2, eps, scale, ref acc);
                    Step(ref a, ref b, ref c, ref d, k2, k1, eps, scale, ref acc);

                    Step(ref a, ref b, ref c, ref d, k1, k2, eps, scale, ref acc);
                    Step(ref a, ref b, ref c, ref d, k2, k1, eps, scale, ref acc);
                    Step(ref a, ref b, ref c, ref d, k1, k2, eps, scale, ref acc);
                    Step(ref a, ref b, ref c, ref d, k2, k1, eps, scale, ref acc);
                }
                for (; i < InnerIterations; i++)
                {
                    Step(ref a, ref b, ref c, ref d, k1, k2, eps, scale, ref acc);
                }
            }

            // Свёртка в «зонд» - конечное число, которым дешево проверять корректность.
            // Комбинация add/mul/div/√ - и снова чисто FPU.
            double mix = (a + b) * (c + d);
            mix = Math.Sqrt(Math.Abs(mix) + eps) / (1.0 + Math.Abs(a * d - b * c) + eps);

            _finiteProbe = mix + acc * 1e-9; // чуть-чуть включаем acc, чтобы цепочку сохранять «живой»
            _acc = acc;
            _passed = true;
        }

        /// <summary>
        /// «Микрошаг» чисто FP: FMA (если доступно) + add/mul/div/sqrt; без ветвлений и памяти.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Step(ref double a, ref double b, ref double c, ref double d,
                                 double k1, double k2, double eps, double scale, ref double acc)
        {
            // FMA даёт максимальную загрузку FPU при наличии поддержки (JIT на x64 с AVX2/FMA сгруппирует в fma*).
            // Если CPU/рантайм не поддерживает - Math.FusedMultiplyAdd деградирует до a*b+c (всё равно FPU).
            a = Math.FusedMultiplyAdd(a, k1, b); // a = a*k1 + b
            b = Math.FusedMultiplyAdd(b, k2, c);
            c = Math.FusedMultiplyAdd(c, k1, d);
            d = Math.FusedMultiplyAdd(d, k2, a);

            // Перемешивание цепочек: суммы/произведения/разности - нагружаем add/mul.
            double ab = a * b + c;
            double cd = c * d + a;
            double bc = b * c - d;

            // Контролируем рост: деление и sqrt нагружают соответствующие блоки, а scale не даёт разлететься.
            a = (Math.Sqrt(Math.Abs(ab)) + eps) * scale;
            b = (Math.Sqrt(Math.Abs(cd)) + eps) * scale;
            c = (Math.Abs(bc) + eps) / (1.0 + ab * k1 + eps);
            d = (Math.Abs(ab - cd) + eps) / (1.0 + bc * k2 + eps);

            // Сводный аккумулятор - без ветвлений, чисто FP.
            acc = Math.FusedMultiplyAdd(acc, 0.9999999, a + b * 1e-6 + c * 1e-9 + d * 1e-12);
        }

        /// <summary>
        /// Лёгкая и детерминированная проверка корректности:
        /// 1) результат горячего цикла должен быть конечным (не NaN/Inf),
        /// 2) сумма 1..1000, посчитанная в double, должна быть ровно 500500 (это точно представимо в double).
        /// </summary>
        public void Validate()
        {
            if (double.IsNaN(_finiteProbe) || double.IsInfinity(_finiteProbe))
                _passed = false;

            // Проверка «чистой» double-арифметики: сложение целых в диапазоне точного представления.
            double s = 0.0;
            for (int i = 1; i <= 1000; i++) s += i;
            if (s != 500_500.0) // это сравнение допустимо: сумма точно представима в double
                _passed = false;

            if (!_passed)
                throw new Exception("Ошибка вычислений с плавающей точкой: обнаружен NaN/Inf или нарушение точной суммы 1..1000.");
        }
    }
}
