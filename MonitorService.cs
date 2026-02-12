/*
 * Проект:   BURST(Brutal Utilization & Resilience Stress Testing)
 * Автор:    Дубинский Артем {dub}
 */

using BURST.Utils;
using BURST.Utils.Log;
using BURST.Utils.Log.Types;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BURST
{
    /// <summary>
    /// Сервис мониторинга и вывода сводной информации по системе во время теста.
    /// <para>Отвечает за:
    /// <list type="bullet">
    ///   <item><description>инициализацию/«прогрев» <see cref="PerformanceCounter"/> по каждому логическому ядру;</description></item>
    ///   <item><description>1 Гц-логирование загрузки CPU и температур в консоль и файл (JSONL через <see cref="PerformanceLogger"/>)</description></item>
    ///   <item><description>необязательный пиннинг потока монитора к одному ядру (если <c>AppState.UiCore</c> не <c>null</c>);</description></item>
    ///   <item><description>онлайн-вывод ошибок из <c>AppState.ErrorQueue</c> и агрегированной сводки из <see cref="ErrorState"/>.</description></item>
    /// </list>
    /// </para>
    /// <remarks>
    /// Сигнал готовности (<see cref="AppState.MonitorReady"/>) подаётся после создания и «прогрева» счётчиков.
    /// Ошибки внутри сервиса не блокируют запуск тестов: при исключении сигнал готовности всё равно выставляется.
    /// </remarks>
    /// </summary>
    internal sealed class MonitorService
    {
        // ======== P/Invoke для аффинити монитора (опционально, если задано UiCore) ========
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern UIntPtr SetThreadAffinityMask(IntPtr hThread, UIntPtr dwThreadAffinityMask);

        /// <summary>
        /// Главный цикл монитора (итерация ~1 раз в секунду).
        /// Безопасен к исключениям - при падении выводит ошибку и не мешает завершению.
        /// </summary>
        public void Run()
        {
            try
            {
                if (AppState.UiCore.HasValue)
                {
                    ulong mask64 = 1UL << AppState.UiCore.Value;
                    var res = SetThreadAffinityMask(GetCurrentThread(), (UIntPtr)mask64);
                    if (res == UIntPtr.Zero)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"[Предупреждение] Не удалось установить аффинити монитора на ядро {AppState.UiCore.Value} (Win32: {Marshal.GetLastWin32Error()})");
                        Console.ResetColor();
                    }
                }

                //TODO 
                int coreCount = Environment.ProcessorCount;
                var cpuCounters = new PerformanceCounter[coreCount];
                for (int i = 0; i < coreCount; i++)
                {
                    cpuCounters[i] = new PerformanceCounter("Processor", "% Processor Time", i.ToString());
                    cpuCounters[i].NextValue();
                }

                using CpuInfoReader cpuInfo = new CpuInfoReader();
                string cpuName = cpuInfo.GetCpuName();

                PerformanceLogger.Initialize("log.json");

                Thread.Sleep(800);
                AppState.MonitorReady.Set();

                const int barLength = 20;

                while (!AppState.CancelRequested)
                {
                    Console.Clear();
                    Console.WriteLine("=== Статистика загрузки CPU ===");
                    Console.WriteLine($"CPU: {cpuName}\n");

                    var temperatureDict = cpuInfo.GetTemperatures();
                    int tempCount = temperatureDict.Count();
                    var perfData = new PerformanceData
                    {
                        Timestamp = DateTime.Now,
                        CpuName = cpuName,
                        CoreUsages = new List<float>(coreCount),

                        //Temperatures = temperatureDict,
                        Errors = new ErrorSummary
                        {
                            Started = ErrorState.Started == 1,
                            Total = Interlocked.Read(ref ErrorState.Total),
                            Top = ErrorState.Map
                            .OrderByDescending(kv => kv.Value.Consecutive)
                                .ThenByDescending(kv => kv.Value.LastAt)
                                    .Take(5)
                                        .Select(kv => new ErrorEntry
                                        {
                                            Key = kv.Key,
                                            Consecutive = kv.Value.Consecutive,
                                            Total = kv.Value.Total,
                                            LastAt = kv.Value.LastAt == DateTime.MinValue ? "-" : kv.Value.LastAt.ToString("HH:mm:ss"),
                                            LastMessage = kv.Value.LastMessage
                                        }).ToList()
                        }

                    };

                    

                    int maxRows = Math.Max(coreCount, tempCount);

                    for (int i = 0; i < maxRows; i++)
                    {
                        string loadInfo = "";
                        string tempInfo = "";
                        ConsoleColor loadColor = Console.ForegroundColor;
                        ConsoleColor tempColor = Console.ForegroundColor;

                        if (i < coreCount)
                        {
                            float usage = cpuCounters[i].NextValue();
                            perfData.CoreUsages!.Add(usage);
                            int filled = Math.Clamp((int)(usage / 100f * barLength), 0, barLength);
                            string bar = new string('#', filled) + new string('-', barLength - filled);
                            loadInfo = $"Поток {i}: {usage,5:F1}% [{bar}]";

                            loadColor = usage <= 50 ? ConsoleColor.Green
                                     : usage <= 75 ? ConsoleColor.Yellow
                                     : ConsoleColor.Red;
                        }

                        if (i < tempCount)
                        {
                            var tempPair = temperatureDict.ElementAt(i);
                            float t = Convert.ToSingle(tempPair.Value);
                            tempInfo = $"{tempPair.Key}: {t}°C";

                            tempColor = t <= 50 ? ConsoleColor.Green
                                      : t <= 79 ? ConsoleColor.Yellow
                                      : ConsoleColor.Red;
                        }

                        Console.ForegroundColor = loadColor;
                        Console.Write("{0,-50}", loadInfo);
                        Console.ResetColor();
                        Console.Write(" ");
                        Console.ForegroundColor = tempColor;
                        Console.WriteLine(tempInfo);
                        Console.ResetColor();
                    }

                    // === Прогресс тестов: снимаем кадр для лога и одновременно печатаем таблицу ===
                    Console.WriteLine("\n=== Прогресс тестов (по ядрам) ===");
                    foreach (var kv in AppState.TestProgressMap.OrderBy(k => k.Key))
                    {
                        int core = kv.Key;
                        var list = kv.Value;

                        // Снимок в DTO (избегаем гонок данных - создаём новые объекты)
                        var dtoList = new List<TestProgress>(list.Count);
                        foreach (var p in list)
                        {
                            string errKey = $"{p.Core}:{p.TestName}";
                            bool hasError = ErrorState.Map.TryGetValue(errKey, out var counters) && counters.Total > 0;

                            bool testFinshed = false;

                            if (p.CompletedCycle == p.Total && !hasError)
                                testFinshed = true;

                            dtoList.Add(new TestProgress
                            {
                                TestName = p.TestName,
                                CompletedCycle = p.CompletedCycle,
                                Core = p.Core,
                                Total = p.Total,
                                IsActive = p.IsActive,
                                Error = hasError,
                                IsFinished = testFinshed

                            });
                        }
                        perfData.TestProgress[core] = dtoList;

                        // Печать в консоль
                        Console.WriteLine($"Ядро {core}:");
                        Console.WriteLine($"  {"Тест",-35} {"Циклы",10}");
                        Console.WriteLine($"  {"".PadLeft(35, '-')} {"".PadLeft(10, '-')}");
                        foreach (var p in list)
                        {
                            if (p.IsActive) Console.ForegroundColor = ConsoleColor.Green;
                            Console.WriteLine($"  {p.TestName,-35} {p.CompletedCycle,3}/{p.Total,-6}");
                            if (p.IsActive) Console.ResetColor();
                        }
                        Console.WriteLine();
                    }



                    // Лог в файл: теперь включает и прогресс, и ошибки
                    PerformanceLogger.LogPerformance(perfData);

                    // Выгружаем сообщения об ошибках в консоль (без изменений)
                    if (!AppState.ErrorQueue.IsEmpty)
                    {
                        Console.WriteLine("=== Обнаруженные ошибки ===");
                        while (AppState.ErrorQueue.TryDequeue(out Exception? ex ))
                        {
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine(ex.Message);
                            Console.ResetColor();
                        }
                        Console.WriteLine();
                    }

                    // Сводка ErrorState на экране (как было)
                    if (ErrorState.Started == 1)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine("=== ВНИМАНИЕ: ОБНАРУЖЕНЫ ОШИБКИ В ТЕСТАХ ===");
                        Console.ResetColor();

                        var top = ErrorState.Map
                            .OrderByDescending(kv => kv.Value.Consecutive)
                            .ThenByDescending(kv => kv.Value.LastAt)
                            .Take(5);

                        foreach (var kv in top)
                        {
                            var key = kv.Key; var c = kv.Value;
                            string when = c.LastAt == DateTime.MinValue ? "-" : c.LastAt.ToString("HH:mm:ss");
                            Console.WriteLine($"  {key,-30} | series={c.Consecutive,-3} total={c.Total,-5} last={when}  {c.LastMessage}");
                        }

                        Console.WriteLine($"Всего ошибок: {System.Threading.Interlocked.Read(ref ErrorState.Total)}");
                    }

                    Console.WriteLine("\nНажмите Enter или Ctrl+C для завершения теста...");
                    Thread.Sleep(1000);
                }
            }
            catch (Exception ex)
            {
                AppState.ErrorQueue.Enqueue(ex);
                Console.WriteLine($"[Ошибка монитора] {ex.Message}");
                AppState.MonitorReady.Set();
            }
            finally
            {
                try { PerformanceLogger.Close(); } catch { /* ignore */ }
            }
        }
    }
}

