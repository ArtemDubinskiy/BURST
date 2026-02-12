/*
 * Проект:   BURST(Brutal Utilization & Resilience Stress Testing)
 * Автор:    Дубинский Артем {dub}
 */

using BURST.Types.Base;       // для IStressTest (тип в RunOptions)
using BURST.Utils;            // для CpuInfoReader
using System.Management;      // для GetPhysicalCoreCount (WMI)
using System.Text;            // для Console.OutputEncoding

namespace BURST
{
    /// <summary>
    /// Главный класс приложения и точка входа.
    /// <para>Оркестрирует жизненный цикл приложения:
    /// <list type="number">
    ///   <item><description>печатает баннер и базовую инфо о CPU;</description></item>
    ///   <item><description>настраивает мягкое завершение по Ctrl+C;</description></item>
    ///   <item><description>парсит аргументы CLI и при необходимости допрашивает недостающее;</description></item>
    ///   <item><description>валидирует ввод;</description></item>
    ///   <item><description>запускает монитор в отдельном высокоприоритетном потоке и ждёт готовности;</description></item>
    ///   <item><description>запускает нагрузочные потоки на выбранных ядрах;</description></item>
    ///   <item><description>ждёт Enter/Ctrl+C, затем корректно останавливает все потоки.</description></item>
    /// </list>
    /// </para>
    /// <remarks>
    /// Резервирование ядра под монитор <b>удалено</b>; поле <c>AppState.UiCore</c> остаётся для будущего расширения.
    /// </remarks>
    /// </summary>
    internal class Program
    {
        /// <summary>
        /// Точка входа (single-threaded STA/модель по умолчанию CLR).
        /// </summary>
        private static void Main(string[] args)
        {

            var v1 = DateTimeOffset.UtcNow.AddMinutes(-25).ToUnixTimeSeconds();
            var v2 = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
            // --- 0) Настройка консоли на UTF-8 для корректного вывода ---
            Console.OutputEncoding = Encoding.UTF8;

            // --- 1) Баннер/заголовок ---
            Cli.WriteBanner();

            // --- 2) Мягкая отмена по Ctrl+C (CancelKeyPress) ---
            // Перехватываем Ctrl+C и переводим приложение в «мягкую остановку»:
            // выставляем флаг, чтобы рабочие циклы завершились сами.
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;                       // предотвращаем жёсткое убийство процесса
                AppState.CancelRequested = true;       // просим все циклы завершиться
            };

            // --- 3) Краткая информация о CPU ---
            using (CpuInfoReader cpuInfo = new CpuInfoReader())
            {
                string cpuName = cpuInfo.GetCpuName();
                Console.ForegroundColor = (ConsoleColor)6;
                Console.WriteLine(cpuName);
                Console.WriteLine($"Обнаружено физических ядер: {GetPhysicalCoreCount()}");
                Console.WriteLine($"Обнаружено логических ядер: {Environment.ProcessorCount}\n");
                Console.ResetColor();
            }

            // --- 4) Парсинг CLI (новый и старый стиль) ---
            var opts = Cli.TryParseArgs(args);

            // Флаги быстрой помощи
            if (opts.ShowHelp)
            {
                Cli.PrintUsage();
                return;
            }
            if (opts.ListTests)
            {
                Cli.PrintTestCatalog();
                return;
            }

            // --- 5) Допрос недостающих опций интерактивно ---
            Cli.PromptMissingOptions(opts);

            // --- 6) Валидация введённых опций ---
            if (opts.CoreIndices is null || opts.CoreIndices.Count == 0)
            {
                Console.WriteLine("Не выбрано ни одно валидное ядро. Завершение работы.");
                return;
            }
            if (opts.SelectedTests is null || opts.SelectedTests.Count == 0)
            {
                Console.WriteLine("Не выбран ни один тест. Завершение работы.");
                return;
            }
            if (opts.CyclesPerTest is null || opts.CyclesPerTest.Count != opts.SelectedTests.Count)
            {
                Console.WriteLine("Количество циклов должно соответствовать числу выбранных тестов. Завершение работы.");
                return;
            }


            // Резервирование ядра под монитор отключено: AppState.UiCore == null

            Console.WriteLine("\nЗапуск монитора...");

            // --- 7) Старт монитора в отдельном потоке (Highest) ---
            var monitor = new MonitorService();
            var monitorThread = new Thread(monitor.Run)
            {
                IsBackground = true,                  // не блокировать завершение процесса
                Priority = ThreadPriority.Highest
            };
            monitorThread.Start();

            // Дожидаемся сигнализации готовности (или предупреждаем, если это дольше 3 сек)
            if (!AppState.MonitorReady.Wait(TimeSpan.FromSeconds(3)))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Предупреждение: монитор инициализируется дольше обычного...");
                Console.ResetColor();
            }

            Console.WriteLine("\nЗапуск нагрузки на выбранных ядрах...");

            // --- 8) Запуск рабочих потоков нагрузки (по 1 на LP из списка) ---
            var stressThreads = new List<Thread>(opts.CoreIndices.Count);
            foreach (int coreIndex in opts.CoreIndices)
            {
                // Локальные копии, чтобы не делиться изменяемыми коллекциями между потоками
                var testsCopy = new List<IStressTest>(opts.SelectedTests);
                var cyclesCopy = new List<int>(opts.CyclesPerTest);

                // Каждый поток закрепляется на своём LP внутри StressRunner.StartOnCore
                var t = new Thread(() => StressRunner.StartOnCore(coreIndex, testsCopy, cyclesCopy, opts.Mode))
                {
                    IsBackground = true,
                    Priority = ThreadPriority.AboveNormal
                };
                t.Start();
                stressThreads.Add(t);
            }

            // --- 9) Ожидание завершения по действию пользователя ---
            Console.WriteLine("Нажмите Enter или Ctrl+C для завершения теста...");
            Console.ReadLine();                       // блокирующее ожидание Enter
            AppState.CancelRequested = true;          // просим рабочие циклы завершиться

            // --- 10) Корректная остановка: ждём все потоки ---
            foreach (var t in stressThreads) t.Join();
            monitorThread.Join();

            // --- 11) Финальные сообщения и выход ---
            Console.WriteLine("Тест завершён. Нажмите Enter для выхода.");
            Console.ReadLine();
        }

        /// <summary>
        /// Возвращает суммарное число физических ядер по данным WMI.
        /// <remarks>
        /// Контейнер <c>Win32_Processor</c>, поле <c>NumberOfCores</c>.
        /// На много-сокетных системах значения суммируются.
        /// </remarks>
        /// Можно переделать на универсальный метод
        /// </summary>
        private static int GetPhysicalCoreCount()
        {
            int coreCount = 0;
            using var moSearcher = new ManagementObjectSearcher("select NumberOfCores from Win32_Processor");
            foreach (var mo in moSearcher.Get())
                coreCount += Convert.ToInt32(mo["NumberOfCores"]);
            return coreCount;
        }
    }
}
