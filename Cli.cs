/*
 * Проект:   BURST(Brutal Utilization & Resilience Stress Testing)
 * Автор:    Дубинский Артем {dub}
 */

using BURST.Types.Base;
using System.Text;
using static BURST.Types.Base.IStressTest;

namespace BURST
{
    /// <summary>
    /// Подсистема командной строки и интерактивного ввода.
    /// <para>Отвечает за:
    /// <list type="bullet">
    ///   <item><description>баннер/оформление</description></item>
    ///   <item><description>парсинг аргументов (новый стиль - <c>--cores</c>/<c>--tests</c>; старый - позиционный)</description></item>
    ///   <item><description>вывод Usage/каталога тестов</description></item>
    ///   <item><description>интерактивный «допрос» недостающих опций</description></item>
    ///   <item><description>парсер списка ядер (all/even/odd/диапазоны/комбинации)</description></item>
    /// </list>
    /// </para>
    /// <remarks> </remarks>
    /// </summary>
    internal static class Cli
    {
        // Каталог тестов: используется для подсказок и в интерактивном выборе.
        private static readonly (int Id, string Name)[] TestCatalog =
        {
            (1,  "Целочисленные вычисления"),
            (2,  "Вычисления с плавающей точкой"),
            (3,  "Работа с памятью"),
            (4,  "Стресс-тест SSE"),
            (5,  "Стресс-тест AVX"),
            (6,  "Стресс-тест AVX2"),
            (7,  "Хеширование SHA-256/512"),
            (8, "Тест имитирующий игровую нагрузку"),
        };

        /// <summary> 
        /// Печатает баннер приложения с сохранением оригинальной цветовой схемы.
        /// </summary>
        public static void WriteBanner()
        {
            Console.OutputEncoding = Encoding.UTF8; // корректный вывод псевдографики и кириллицы
            Console.ForegroundColor = (ConsoleColor)5;
            Console.WriteLine("=== Brutal Utilization & Resilience Stress Testing ===");
            Console.WriteLine("    ||  ▄▄▄▄·    ▄• ▄▌   ▄▄▄    .▄▄ ·   ▄▄▄▄▄▄  ||");
            Console.WriteLine("    ||  ▐█ ▀█▪   █▪ █▌   ▀▪ █   ▐█ ▀.    •██    ||");
            Console.WriteLine("    ||  ▐█▀▀█▄   █▌▐█▌   ▐▀▀▄   ▄▀▀▀█▄    ▀█.▪  ||");
            Console.WriteLine("    ||  █▄▪ ██   ▐█▄█    ▐█•█▌  ▐█▄▪▐█    ▐█▌·  ||");
            Console.WriteLine("    || ·▀▀▀▀  ▀ . ▀▀▀ ▀ .▀  ▀ ▀  ▀▀▀▀  ▀  ▀▀ ▀  ||");
            Console.ForegroundColor = (ConsoleColor)2;
            Console.WriteLine("    || ============= B.R.U.S.T. =============== ||");
            Console.WriteLine("    ||     === CPU Stress Test Utility ===      ||\n");
            Console.ResetColor();
        }

        /// <summary>
        /// Парсит аргументы командной строки.
        /// <para>Поддерживается:
        /// <list type="bullet">
        ///   <item><description> Стиль аргументов: <c>--cores &lt;...&gt;</c>, <c>--tests &lt;...&gt;</c>, а также флаги <c>--help</c>, <c>--list-tests</c>.</description></item>
        ///   <item><description> <c>&lt;cores&gt; &lt;tests&gt;</c>.</description></item>
        /// </list>
        /// </para>
        /// </summary>
        public static RunOptions TryParseArgs(string[] args)
        {
            var opts = new RunOptions();

            if (args.Length == 0)
                return opts;

            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? pos0 = null, pos1 = null;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a.StartsWith("--"))
                {
                    string key = a;
                    string val = (i + 1 < args.Length && !args[i + 1].StartsWith("--")) ? args[++i] : "true";
                    dict[key] = val;
                }
                else
                {
                    if (pos0 is null) pos0 = a;
                    else if (pos1 is null) pos1 = a;
                }
            }

            // Флаги
            opts.ShowHelp = dict.ContainsKey("--help") || dict.ContainsKey("-h");
            opts.ListTests = dict.ContainsKey("--list-tests");

            // Значения по ключам
            if (dict.TryGetValue("--cores", out var coresStr))
                opts.CoreIndices = ParseCoreIndices(coresStr);

            if (dict.TryGetValue("--tests", out var testsStr))
                opts.SelectedTests = TestManager.GetSelectedTests(testsStr);

            if (dict.TryGetValue("--cycles", out var cyclesStr))
                opts.CyclesPerTest = ParseCycles(cyclesStr);

            if (dict.TryGetValue("--mode", out var modeStr))
            {
                opts.Mode = ParseMode(modeStr);
                opts.ModeProvided = true;
            }

            // Старый стиль: <cores> <tests>
            if (opts.CoreIndices is null && pos0 != null)
                opts.CoreIndices = ParseCoreIndices(pos0);

            if (opts.SelectedTests is null && pos1 != null)
                opts.SelectedTests = TestManager.GetSelectedTests(pos1);

            //TODO
            /*if (dict.TryGetValue("--maxerr", out var maxErrStr) && int.TryParse(maxErrStr, out int maxErr))
                opts.MaxErrors = Math.Max(1, maxErr);*/

            return opts;
        }

        /// <summary>
        /// Интерактивно допрашивает недостающие опции (ядра/тесты).
        /// Валидация ввода происходит в цикле до получения валидного результата.
        /// </summary>
        public static void PromptMissingOptions(RunOptions opts)
        {
            Console.ForegroundColor = ConsoleColor.Blue;

            // Ядра
            if (opts.CoreIndices is null || opts.CoreIndices.Count == 0)
            {
                Console.WriteLine("Введите номера логических ядер через запятую (например: 0,1,2,9,4),");
                Console.Write("или 'all' / 'even' / 'odd' / диапазон вроде 0-7: ");
                Console.ResetColor();

                while (true)
                {
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    string input = Console.ReadLine() ?? string.Empty;
                    Console.ResetColor();

                    var cores = ParseCoreIndices(input);
                    if (cores.Count > 0)
                    {
                        opts.CoreIndices = cores;
                        break;
                    }
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Write("Не удалось распознать ядра. Повторите ввод: ");
                    Console.ResetColor();
                }
            }

            // Тесты
            if (opts.SelectedTests is null || opts.SelectedTests.Count == 0)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("\nВведите номера тестов через запятую (например: 2,9,4). Доступно:");
                PrintTestCatalog();
                Console.Write("Тесты: ");
                Console.ResetColor();

                while (true)
                {
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    string input = Console.ReadLine() ?? string.Empty;
                    Console.ResetColor();

                    var tests = TestManager.GetSelectedTests(input);
                    if (tests.Count > 0)
                    {
                        opts.SelectedTests = tests;
                        break;
                    }
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Write("Не удалось распознать тесты. Повторите ввод: ");
                    Console.ResetColor();
                }
            }

            // Циклы: по одному числу на каждый тест
            if (opts.SelectedTests is not null && (opts.CyclesPerTest is null || opts.CyclesPerTest.Count != opts.SelectedTests.Count))
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("\nДля каждого теста укажите количество циклов (через запятую),");
                Console.WriteLine("например для 4 тестов: 100,100,100,100");
                Console.Write("Циклы: ");
                Console.ResetColor();

                while (true)
                {
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    string input = Console.ReadLine() ?? string.Empty;
                    Console.ResetColor();

                    var cycles = ParseCycles(input);
                    if (cycles.Count == opts.SelectedTests!.Count && cycles.All(c => c >= 0))
                    {
                        opts.CyclesPerTest = cycles;
                        break;
                    }
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Write($"Ожидалось {opts.SelectedTests!.Count} целых значений ≥ 0. Повторите ввод: ");
                    Console.ResetColor();
                }
            }

            // Режим
            if (!opts.ModeProvided)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("\nВыберите режим выполнения тестов:");
                Console.WriteLine("  seq   - последовательно (каждый тест полностью, затем следующий)");
                Console.WriteLine("  round - по одному циклу от каждого теста по кругу");
                Console.WriteLine("  rand  - случайно по одному циклу до исчерпания");
                Console.Write("Режим [по умолчанию: seq]: ");
                Console.ResetColor();

                Console.ForegroundColor = ConsoleColor.DarkYellow;
                string modeInput = Console.ReadLine() ?? string.Empty;
                Console.ResetColor();

                if (!string.IsNullOrWhiteSpace(modeInput))
                    opts.Mode = ParseMode(modeInput);
            }

            //TODO реализовать 
           /* if (opts.MaxErrors <= 0)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("\nУкажите максимальное количество ошибок после которых тест будет остановлен! ");
                Console.WriteLine("Значение общее для всех тестов!");
                Console.WriteLine("например: 10");
                Console.Write("Максимальное коллиство ошибок: ");
                Console.ResetColor();

                while (true)
                {
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    string input = Console.ReadLine() ?? string.Empty;
                    Console.ResetColor();

                    var maxError = Convert.ToInt32(input);
                    if (maxError >= 0 )
                    {
                        opts.MaxErrors = maxError;
                        break;
                    }
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Write($"Ожидалось число ≥ 0. Повторите ввод: ");
                    Console.ResetColor();
                }
            }*/
        }

        /// <summary>
        /// Краткая справка по использованию (Usage).
        /// </summary>
        public static void PrintUsage()
        {
            Console.WriteLine("Использование:");
            Console.WriteLine("  BURST.exe --cores <cores> --tests <tests> --cycles <cycles> --mode <seq|round|rand>");
            Console.WriteLine("  BURST.exe <cores> <tests>              (совместимость; cycles/mode спросим интерактивно)");
            Console.WriteLine();
            Console.WriteLine("  <cores>  : 'all' | 'even' | 'odd' | список (0,1,2) | диапазон (0-7) | комбинация (0-3,6,8-10)");
            Console.WriteLine("  <tests>  : номера тестов через запятую. См. --list-tests");
            Console.WriteLine("  <cycles> : столько же чисел, сколько тестов; каждое - число циклов для соответствующего теста (0 допускается)");
            Console.WriteLine("  <mode>   : seq - последовательно (каждый тест полностью, затем следующий),  ");
            Console.WriteLine("             round - по одному циклу от каждого теста по кругу,");
            Console.WriteLine("             rand - случайно по одному циклу до исчерпания циклов");
            Console.WriteLine("  --maxerr <N> : завершить тесты после N ошибок (по умолчанию: 1)");
            Console.WriteLine();
            Console.WriteLine("Пример:");
            Console.WriteLine("  CpuStressTestApp.exe --cores 0-7 --tests 1,2,3,4 --cycles 100,100,100,100 --mode round");
            Console.WriteLine();
        }

        /// <summary>
        /// Печатает доступный каталог тестов (формат "Id - Name").
        /// </summary>
        public static void PrintTestCatalog()
        {
            foreach (var t in TestCatalog)
                Console.WriteLine($"{t.Id,2} - {t.Name}");
        }

        /// <summary>
        /// Разбирает строку с описанием набора логических ядер.
        /// Поддерживаются ключевые слова (<c>all</c>/<c>even</c>/<c>odd</c>), списки/диапазоны и их комбинации.
        /// Корректные индексы фильтруются по <see cref="Environment.ProcessorCount"/>, дубликаты удаляются, результат сортируется.
        /// </summary>
        public static List<int> ParseCoreIndices(string? input)
        {
            var result = new List<int>();
            if (string.IsNullOrWhiteSpace(input)) return result;

            input = input.Trim().ToLowerInvariant();
            int max = Environment.ProcessorCount;

            if (input == "all")
            {
                for (int i = 0; i < max; i++) result.Add(i);
                return result;
            }
            if (input == "even")
            {
                for (int i = 0; i < max; i += 2) result.Add(i);
                return result;
            }
            if (input == "odd")
            {
                for (int i = 1; i < max; i += 2) result.Add(i);
                return result;
            }

            foreach (string token in input.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.Contains('-'))
                {
                    var parts = token.Split('-', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2 && int.TryParse(parts[0], out int a) && int.TryParse(parts[1], out int b))
                    {
                        if (a > b) (a, b) = (b, a);
                        for (int i = a; i <= b; i++)
                            if (i >= 0 && i < max) result.Add(i);
                    }
                }
                else if (int.TryParse(token, out int idx))
                {
                    if (idx >= 0 && idx < max) result.Add(idx);
                }
            }

            // Дедупликация и сортировка по возрастанию
            return result.Distinct().OrderBy(x => x).ToList();

        }

        /// <summary>
        /// Разбор строки циклов: "100, 200, 0, 50"  [100,200,0,50].
        /// Невалидные/отрицательные значения игнорируем.
        /// </summary>
        public static List<int> ParseCycles(string? input)
        {
            var result = new List<int>();
            if (string.IsNullOrWhiteSpace(input)) return result;

            foreach (var token in input.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(token, out int v) && v >= 0)
                    result.Add(v);
            }
            return result;
        }

        /// <summary>
        /// Парсинг режима исполнения.
        /// </summary>
        public static ExecutionMode ParseMode(string? input)
        {
            input = (input ?? string.Empty).Trim().ToLowerInvariant();
            return input switch
            {
                "seq" or "sequential" or "1" => ExecutionMode.SequentialByTest,
                "round" or "rr" or "2" => ExecutionMode.RoundRobinByCycle,
                "rand" or "random" or "3" => ExecutionMode.Random,
                _ => ExecutionMode.SequentialByTest
            };
        }
    }

    /// <summary>
    /// Объект транспортировки параметров запуска (результат парсинга CLI + интерактивного ввода).
    /// </summary>
    internal sealed class RunOptions
    {
        /// <summary>Список логических ядер для запуска нагрузки.</summary>
        public List<int>? CoreIndices { get; set; }

        /// <summary>Выбранные тесты как инстансы IStressTest.</summary>
        public List<IStressTest>? SelectedTests { get; set; }

        /// <summary>Количество циклов для каждого соответствующего теста.</summary>
        public List<int>? CyclesPerTest { get; set; }

        /// <summary>Режим планирования выполнения набора тестов.</summary>
        public ExecutionMode Mode { get; set; } = ExecutionMode.SequentialByTest;

        public bool ModeProvided { get; set; } = false;

        /// <summary>Флаги справки/каталога.</summary>
        public bool ShowHelp { get; set; }
        public bool ListTests { get; set; }


        /// <summary>
        /// Максимально допустимое количество ошибок. 
        /// Если превышено, тесты завершаются.
        /// По умолчанию = 1).
        /// </summary>
       // public int MaxErrors { get; set; } = 1;
    }
}
