/*
 * Проект:   BURST(Brutal Utilization & Resilience Stress Testing)
 * Автор:    Дубинский Артем {dub}
 */

using BURST.Types.Base;
using BURST.Utils.Log.Types;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static BURST.Types.Base.IStressTest;

namespace BURST
{
    /// <summary>
    /// Исполнитель нагрузки на конкретном логическом ядре.
    /// <para>Выполняет:
    /// <list type="bullet">
    ///   <item><description>пиннинг текущего потока к заданному LP (через WinAPI <c>SetThreadAffinityMask</c>);</description></item>
    ///   <item><description>основной цикл тестирования: для каждого выбранного теста - <c>RunTest()</c>  <c>Validate()</c>;</description></item>
    ///   <item><description>обработку ошибок тестов: регистрация в <see cref="ErrorState"/> и остановка текущего потока;</description></item>
    ///   <item><description>публикацию необработанных исключений в <c>AppState.ErrorQueue</c> (для монитора).</description></item>
    /// </list>
    /// </para>
    /// <remarks>
    /// Резервирование отдельного ядра под монитор удалено. Пиннинг касается только рабочих потоков.
    /// Для систем с числом LP &gt; 64 потребуется другая API (<c>SetThreadGroupAffinity</c>), здесь используется 64-битная маска.
    /// </remarks>
    /// </summary>
    internal static class StressRunner
    {
        // ======== P/Invoke (Windows) для установки аффинити потока ========
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern UIntPtr SetThreadAffinityMask(IntPtr hThread, UIntPtr dwThreadAffinityMask);

        private static readonly ConcurrentDictionary<(int Core, string Test), (int Cycle, int Total, bool IsActive)> _states
        = new();

        /// <summary>
        /// Запускает список стресс-тестов на указанном логическом ядре с учётом циклов и режима.
        /// </summary>
        /// <param name="coreIndex">Логический индекс LP (0..N-1).</param>
        /// <param name="tests">Инстансы тестов (порядок важен для seq/round).</param>
        /// <param name="cyclesPerTest">Количество циклов на каждый тест (соответствует индексу в <paramref name="tests"/>).</param>
        /// <param name="mode">Режим планирования.</param>
        public static void StartOnCore(int coreIndex, List<IStressTest> tests, List<int> cyclesPerTest, ExecutionMode mode)
        {
            try
            {
                Thread.BeginThreadAffinity();

                // Устанавливаем аффинити на заданный LP (64-бит маска)
                ulong mask64 = 1UL << coreIndex;
                var res = SetThreadAffinityMask(GetCurrentThread(), (UIntPtr)mask64);
                if (res == UIntPtr.Zero)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"[Предупреждение] Не удалось установить аффинити для ядра {coreIndex} (код Win32: {Marshal.GetLastWin32Error()})");
                    Console.ResetColor();
                }

                Console.WriteLine($"[Поток] Ядро {coreIndex}: старт {mode}, tests={tests.Count}");

                // Нормализуем циклы (отрицательные 0)
                var progress = new List<TestProgress>(tests.Count);
                for (int i = 0; i < cyclesPerTest.Count; i++)
                {
                    if (cyclesPerTest[i] < 0) cyclesPerTest[i] = 0;
                    progress.Add(new TestProgress
                    {
                        TestName = tests[i].GetType().Name,
                        Core = coreIndex,
                        Total = cyclesPerTest[i],
                        CompletedCycle = 0,
                        IsActive = false
                    });
                }
                AppState.TestProgressMap.AddOrUpdate(coreIndex, progress, (_, __) => progress);

                // Запуск по выбранному режиму.
                switch (mode)
                {
                    case ExecutionMode.SequentialByTest:
                        RunSequential(coreIndex, tests, cyclesPerTest, progress);
                        break;

                    case ExecutionMode.RoundRobinByCycle:
                        RunRoundRobin(coreIndex, tests, cyclesPerTest, progress);
                        break;

                    case ExecutionMode.Random:
                        RunRandom(coreIndex, tests, cyclesPerTest, progress);
                        break;
                }
            }
            catch (Exception ex)
            {
                AppState.ErrorQueue.Enqueue(ex);
                Console.WriteLine($"[Ошибка] В потоке ядра {coreIndex}: {ex.Message}");
            }
            finally
            {
                // Снимаем подсветку и оставляем финальные значения Completed.
                if (AppState.TestProgressMap.TryGetValue(coreIndex, out var list))
                    foreach (var p in list) p.IsActive = false;
            }
        }

        /// <summary>
        /// Режим 1: каждый тест выполняет все свои циклы полностью, затем следующий.
        /// </summary>
        private static void RunSequential(int coreIndex, List<IStressTest> tests, List<int> cycles, List<TestProgress> prog)
        {
            for (int i = 0; i < tests.Count && !AppState.CancelRequested; i++)
            {
                var test = tests[i];
                int count = cycles[i];
                if (count <= 0) continue;

                for (int c = 0; c < count && !AppState.CancelRequested; c++)
                {
                    SetActive(prog, i, true);
                    bool ok = RunOneCycle(coreIndex, test);
                    if (!ok) return; // при ошибке - остановка потока
                    prog[i].CompletedCycle++;
                    SetActive(prog, i, false);
                    Thread.Yield();
                }
            }
        }

        /// <summary>
        /// Режим 2: по одному циклу каждого теста по кругу, пока у всех не закончатся циклы.
        /// </summary>
        private static void RunRoundRobin(int coreIndex, List<IStressTest> tests, List<int> cycles, List<TestProgress> prog)
        {
            var remaining = cycles.ToArray();
            int alive = remaining.Count(x => x > 0);

            while (alive > 0 && !AppState.CancelRequested)
            {
                for (int i = 0; i < tests.Count && !AppState.CancelRequested; i++)
                {
                    if (remaining[i] <= 0) continue;

                    SetActive(prog, i, true);
                    if (!RunOneCycle(coreIndex, tests[i])) return;
                    prog[i].CompletedCycle++;
                    remaining[i]--;
                    SetActive(prog, i, false);

                    if (remaining[i] == 0) alive--;
                    Thread.Yield();
                }
            }
        }

        /// <summary>
        /// Режим 3: случайный выбор теста с оставшимися циклами; выполняется ровно один цикл.
        /// </summary>
        private static void RunRandom(int coreIndex, List<IStressTest> tests, List<int> cycles, List<TestProgress> prog)
        {
            var remaining = cycles.ToArray();
            int totalLeft = remaining.Sum();
            if (totalLeft == 0) return;

            // Инициализируем генератор с «перемешанным» сидом
            var rng = new Random(unchecked(Environment.TickCount * 31 + coreIndex));

            while (totalLeft > 0 && !AppState.CancelRequested)
            {
                // Кандидаты с остатком > 0
                var candidates = new List<int>(tests.Count);
                for (int i = 0; i < remaining.Length; i++)
                    if (remaining[i] > 0) candidates.Add(i);

                if (candidates.Count == 0) break;

                int pick = candidates[rng.Next(candidates.Count)];

                SetActive(prog, pick, true);
                if (!RunOneCycle(coreIndex, tests[pick])) return;
                prog[pick].CompletedCycle++;
                remaining[pick]--;
                totalLeft--;
                SetActive(prog, pick, false);

                Thread.Yield();
            }
        }

        /// <summary>Отмечает активный тест и гасит подсветку у остальных.</summary>
        private static void SetActive(List<TestProgress> prog, int idx, bool active)
        {
            for (int i = 0; i < prog.Count; i++)
                prog[i].IsActive = active && i == idx;
        }

        /// <summary>
        /// Выполняет один цикл: RunTest, Validate с обработкой ошибок/учётом статистики.
        /// </summary>
        /// <returns>true - если цикл успешен; false - если зафиксирована ошибка и поток должен завершиться.</returns>
        private static bool RunOneCycle(int coreIndex, IStressTest test)
        {
            try
            {
                test.RunTest();
                test.Validate();
                ErrorState.ResetOk(coreIndex, test);
                return true;
            }
            catch (Exception ex)
            {
                ErrorState.Report(coreIndex, test, ex);
                return false; // останавливаем текущий поток ядра

                //TODO
               /* if (Interlocked.Read(ref ErrorState.Total) >= AppState.RunOptions.MaxErrors)
                {
                    AppState.CancelRequested = true;
                   
                } */
            }
        }

       /* public static void ReportCycle(int coreIndex, IStressTest test, int cycle, int total, bool active)
        {
            _states[(coreIndex, test.GetType().Name)] = (cycle, total, active);
        }

        public static List<TestState> GetTestStates()
        {
            return _states.Select(kv => new TestState
            {
                Core = kv.Key.Core,
                Test = kv.Key.Test,
                Cycle = kv.Value.Cycle,
                Total = kv.Value.Total,
                IsActive = kv.Value.IsActive
            }).ToList();
        }*/
    }
}
