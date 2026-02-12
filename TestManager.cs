/*
 * Проект:   BURST(Brutal Utilization & Resilience Stress Testing)
 * Автор:    Дубинский Артем {dub}
 */

using BURST.Types.Base;
using BURST.Types.StressTests;

namespace BURST
{
    /// <summary>
    /// Фабрика выбора тестов по числовым идентификаторам.
    /// <para>Принимает пользовательскую строку с номерами (разделители: запятая/пробел)
    /// и возвращает список инстансов тестов <see cref="IStressTest"/>.</para>
    /// <remarks>
    /// Неизвестные/некорректные номера пропускаются с сообщением в консоль.
    /// При необходимости легко расширяется добавлением новых case.
    /// </remarks>
    /// </summary>
    public static class TestManager
    {
        /// <summary>
        /// Преобразует строку номеров в список тестов.
        /// </summary>
        /// <param name="input">Например: "1,2,3" или "8 9". Допустимы пробелы и запятые.</param>
        /// <returns>Список тестов, готовых к запуску. Пустой - если валидных номеров не найдено.</returns>
        public static List<IStressTest> GetSelectedTests(string input)
        {
            List<IStressTest> selectedTests = new List<IStressTest>();
            string[] parts = input.Split(new char[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var part in parts)
            {
                if (int.TryParse(part, out int testNumber))
                {
                    switch (testNumber)
                    {
                        case 1:
                            selectedTests.Add(new IntegerStressTest());
                            break;
                        case 2:
                            selectedTests.Add(new FloatingPointStressTest());
                            break;
                        case 3:
                            selectedTests.Add(new MemoryStressTest());
                            break;
                        case 4:
                            selectedTests.Add(new SseCacheStressTest());
                            break;
                        case 5:
                            selectedTests.Add(new AvxCacheStressTest());
                            break;
                        case 6:
                            selectedTests.Add(new Avx2CacheStressTest());
                            break;
                        case 7:
                            selectedTests.Add(new HashingStressTest());
                            break;
                        case 8:
                            selectedTests.Add(new GamingStressTest());
                            break;   
                        case 102030:
                            // Преднамеренно «падающий» тест для проверки 
                            selectedTests.Add(new FailingStressTest());
                            break;

                        default:
                            Console.WriteLine($"Тест с номером {testNumber} не существует. Пропускаем...");
                            break;
                    }
                }
                else
                {
                    Console.WriteLine($"Не удалось распознать тест: {part}. Пропускаем...");
                }
            }

            return selectedTests;
        }
    }
}
