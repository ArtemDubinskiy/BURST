/*
 * Проект:   BURST(Brutal Utilization & Resilience Stress Testing)
 * Автор:    Дубинский Артем {dub}
 */

using System;
using System.Threading;
using BURST.Types.Base;

namespace BURST.Types.StressTests
{
    public sealed class FailingStressTest : IStressTest
    {
        // --- НАСТРОЙКИ ---
        private const int WarmupValidations = 2000; // гарантированный прогрев на КАЖДОМ потоке
        private const double FailProbability = 0.002;

        // Пер-потоковые счётчики
        private static readonly ThreadLocal<int> _validateCounter = new(() => 0, trackAllValues: false);
        private static readonly ThreadLocal<int> _nextFailAt = new(() => 0, trackAllValues: false);
        private static readonly ThreadLocal<bool> _warmed = new(() => false, trackAllValues: false);

        public void RunTest()
        {
            double s = 0;
            for (int i = 0; i < 50_000; i++) s += Math.Sqrt(i);
        }

        public void Validate()
        {
            int n = _validateCounter.Value + 1;
            _validateCounter.Value = n;

            // Прогрев на каждом потоке независимо
            if (!_warmed.Value)
            {
                if (n <= WarmupValidations) return;
                _warmed.Value = true;
            }

            int target = _nextFailAt.Value;
            if (target == 0)
            {
                // первый сбой для ЭТОГО потока - строго после прогрева
                target = WarmupValidations + SampleGeometric(FailProbability);
                _nextFailAt.Value = target;
            }

            if (n >= target)
            {
                // планируем следующий сбой для ЭТОГО потока
                _nextFailAt.Value = n + SampleGeometric(FailProbability);

                throw new Exception($"Синтетический сбой (Validate #{n}). Следующий ориентировочно после #{_nextFailAt.Value}.");
            }
        }

        private static int SampleGeometric(double p)
        {
            if (p <= 0) return int.MaxValue / 2;
            if (p >= 1) return 1;
            double u;
            do { u = Random.Shared.NextDouble(); } while (u == 0.0);
            int k = (int)Math.Ceiling(Math.Log(u) / Math.Log(1.0 - p));
            return (k < 1) ? 1 : k;
        }
    }
}
